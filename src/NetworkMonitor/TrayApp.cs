using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace NetworkMonitor;

/// <summary>The tray icon: live download and upload speed, refreshed every second.</summary>
internal sealed class TrayApp : ApplicationContext
{
    const int TooltipMax = 127; // NotifyIcon.Text throws above this

    readonly NotifyIcon _icon;
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    readonly RateMeter _meter = new();
    readonly Stopwatch _clock = Stopwatch.StartNew();
    readonly string _settingsPath = Settings.DefaultPath;
    readonly ToolStripMenuItem _startAtLogin, _bytes, _bits, _resetColours;

    Settings _settings;
    Palette _palette;
    volatile bool _themeChanged;
    IntPtr _iconHandle;
    string _drawn = "";
    double _down, _up;
    string _networks = "";

    public TrayApp()
    {
        _settings = Settings.Load(_settingsPath);
        _palette = Palette.Current(_settings);

        _bytes = new ToolStripMenuItem("MB/s", null, (_, _) => Apply(_settings with { Bits = false }));
        _bits = new ToolStripMenuItem("Mbps", null, (_, _) => Apply(_settings with { Bits = true }));
        _resetColours = new ToolStripMenuItem("Reset colours", null,
            (_, _) => Apply(_settings with { DownColor = null, UpColor = null }));
        _startAtLogin = new ToolStripMenuItem("Start at login", null, (_, _) => ToggleStartAtLogin())
        {
            Checked = StartupRegistration.IsEnabled(),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("App usage…", null, (_, _) => ShowUsage());
        menu.Items.Add("Open Resource Monitor", null, (_, _) => OpenResourceMonitor());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Units", null, _bytes, _bits));
        menu.Items.Add("Download colour…", null, (_, _) => PickColour(down: true));
        menu.Items.Add("Upload colour…", null, (_, _) => PickColour(down: false));
        menu.Items.Add(_resetColours);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_startAtLogin);
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        RefreshMenu();

        _icon = new NotifyIcon { ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => OpenResourceMonitor();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _timer.Tick += (_, _) => Tick();

        Tick(); // sets the first counters and draws "0", so the icon never shows up blank
        _icon.Visible = true;
        _timer.Start();
    }

    void Tick()
    {
        List<InterfaceSample> rows;
        try
        {
            rows = InterfaceCounters.Read();
        }
        catch (Win32Exception)
        {
            return; // adapters in flux; the clock keeps running, so the next tick covers both seconds
        }
        double seconds = _clock.Elapsed.TotalSeconds;
        _clock.Restart();

        var counted = rows.Where(r => r.Counted).ToList();
        (_down, _up) = _meter.Update(counted, seconds);
        if (_themeChanged)
        {
            _themeChanged = false;
            _palette = Palette.Current(_settings);
        }
        _networks = string.Join(", ", counted.Select(r => r.Alias).Distinct());
        Show();
    }

    void Show()
    {
        bool bits = _settings.Bits;
        Draw(SpeedFormat.Compact(_down, bits), SpeedFormat.IsIdle(_down, bits),
             SpeedFormat.Compact(_up, bits), SpeedFormat.IsIdle(_up, bits));
        string tip = $"↓ {SpeedFormat.Full(_down, bits)}   ↑ {SpeedFormat.Full(_up, bits)}\n"
                   + (_networks.Length > 0 ? _networks : "No network");
        _icon.Text = tip.Length > TooltipMax ? tip[..TooltipMax] : tip;
    }

    void Draw(string down, bool downIdle, string up, bool upIdle)
    {
        Color dc = downIdle ? _palette.Idle : _palette.Down;
        Color uc = upIdle ? _palette.Idle : _palette.Up;
        int size = TrayIconSize();
        string key = $"{down}|{up}|{dc.ToArgb()}|{uc.ToArgb()}|{size}";
        if (key == _drawn) return;
        _drawn = key;

        using var bitmap = TrayIconRenderer.Render(down, dc, up, uc, size);
        IntPtr handle = bitmap.GetHicon();
        Icon? oldIcon = _icon.Icon;
        IntPtr oldHandle = _iconHandle;
        _icon.Icon = Icon.FromHandle(handle); // the shell keeps its own copy
        _iconHandle = handle;
        oldIcon?.Dispose();
        // Icon.FromHandle doesn't own the handle: without this, every redraw leaks a GDI object.
        if (oldHandle != IntPtr.Zero) DestroyIcon(oldHandle);
    }

    /// <summary>
    /// The icon size for the taskbar's display scale, read on every tick, so a scale change
    /// redraws at the new size without a restart. SystemInformation.SmallIconSize keeps the
    /// size from when the app started.
    /// </summary>
    static int TrayIconSize()
    {
        IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
        uint dpi = taskbar != IntPtr.Zero ? GetDpiForWindow(taskbar) : 0;
        return dpi > 0 ? GetSystemMetricsForDpi(SM_CXSMICON, dpi) : SystemInformation.SmallIconSize.Width;
    }

    void Apply(Settings settings)
    {
        _settings = settings;
        _palette = Palette.Current(settings);
        RefreshMenu();
        try
        {
            settings.Save(_settingsPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Couldn't save your settings to {_settingsPath}:\n{e.Message}\n\nThey apply until NetworkMonitor quits.",
                "NetworkMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        _drawn = "";
        Show();
    }

    void RefreshMenu()
    {
        _bytes.Checked = !_settings.Bits;
        _bits.Checked = _settings.Bits;
        _resetColours.Enabled = _settings.DownColor is not null || _settings.UpColor is not null;
    }

    void PickColour(bool down)
    {
        using var dialog = new ColorDialog { Color = down ? _palette.Down : _palette.Up, FullOpen = true };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        string hex = Settings.ToHex(dialog.Color);
        Apply(down ? _settings with { DownColor = hex } : _settings with { UpColor = hex });
    }

    void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) // light/dark switches arrive as General
        {
            _themeChanged = true;
            _drawn = "";
        }
    }

    void ToggleStartAtLogin()
    {
        StartupRegistration.Set(!StartupRegistration.IsEnabled());
        _startAtLogin.Checked = StartupRegistration.IsEnabled();
    }

    UsageForm? _usage;

    void ShowUsage()
    {
        if (_usage is null || _usage.IsDisposed) _usage = new UsageForm();
        _usage.Show();
        _usage.Activate();
    }

    static void OpenResourceMonitor()
    {
        try
        {
            Process.Start(new ProcessStartInfo("resmon.exe") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            // Cancelled at the UAC prompt.
        }
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false; // otherwise a dead icon stays in the tray until the mouse passes over it
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _timer.Dispose();
            _usage?.Dispose();
            _icon.Visible = false;
            _icon.Icon?.Dispose();
            _icon.ContextMenuStrip?.Dispose();
            _icon.Dispose();
        }
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
        base.Dispose(disposing);
    }

    const int SM_CXSMICON = 49;

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);
}

/// <summary>
/// Colours that read on the taskbar: bright on a dark one, deep on a light one, grey when idle.
/// A colour you picked replaces the theme's colour in both modes.
/// </summary>
internal readonly record struct Palette(Color Down, Color Up, Color Idle)
{
    internal static Palette Current(Settings settings) => For(LightTaskbar(), settings);

    internal static Palette For(bool lightTaskbar, Settings settings)
    {
        var theme = lightTaskbar
            ? new Palette(Color.FromArgb(0x00, 0x7A, 0x33), Color.FromArgb(0xA8, 0x52, 0x00), Color.FromArgb(0x70, 0x70, 0x70))
            : new Palette(Color.FromArgb(0x51, 0xFF, 0x70), Color.FromArgb(0xFF, 0xB4, 0x54), Color.FromArgb(0x9A, 0x9A, 0x9A));
        return theme with
        {
            Down = Settings.ParseColor(settings.DownColor) ?? theme.Down,
            Up = Settings.ParseColor(settings.UpColor) ?? theme.Up,
        };
    }

    // The taskbar follows the Windows mode ("SystemUsesLightTheme"), not the app mode.
    static bool LightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }
}
