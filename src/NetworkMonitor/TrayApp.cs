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
    readonly ToolStripMenuItem _startAtLogin;

    Palette _palette = Palette.Current();
    volatile bool _themeChanged;
    IntPtr _iconHandle;
    string _drawn = "";

    public TrayApp()
    {
        _startAtLogin = new ToolStripMenuItem("Start at login", null, (_, _) => ToggleStartAtLogin())
        {
            Checked = StartupRegistration.IsEnabled(),
        };
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Resource Monitor", null, (_, _) => OpenResourceMonitor());
        menu.Items.Add(_startAtLogin);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

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
        var (down, up) = _meter.Update(counted, seconds);

        if (_themeChanged)
        {
            _themeChanged = false;
            _palette = Palette.Current();
        }
        Draw(down, up);

        string networks = string.Join(", ", counted.Select(r => r.Alias).Distinct());
        string tip = $"↓ {SpeedFormat.Full(down)}   ↑ {SpeedFormat.Full(up)}\n{(networks.Length > 0 ? networks : "No network")}";
        _icon.Text = tip.Length > TooltipMax ? tip[..TooltipMax] : tip;
    }

    void Draw(double down, double up)
    {
        string d = SpeedFormat.Compact(down), u = SpeedFormat.Compact(up);
        Color dc = SpeedFormat.IsIdle(down) ? _palette.Idle : _palette.Down;
        Color uc = SpeedFormat.IsIdle(up) ? _palette.Idle : _palette.Up;
        string key = $"{d}|{u}|{dc.ToArgb()}|{uc.ToArgb()}";
        if (key == _drawn) return;
        _drawn = key;

        using var bitmap = TrayIconRenderer.Render(d, dc, u, uc, SystemInformation.SmallIconSize.Width);
        IntPtr handle = bitmap.GetHicon();
        Icon? oldIcon = _icon.Icon;
        IntPtr oldHandle = _iconHandle;
        _icon.Icon = Icon.FromHandle(handle); // the shell keeps its own copy
        _iconHandle = handle;
        oldIcon?.Dispose();
        // Icon.FromHandle doesn't own the handle: without this, every redraw leaks a GDI object.
        if (oldHandle != IntPtr.Zero) DestroyIcon(oldHandle);
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

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);
}

/// <summary>Colours that read on the taskbar: bright on a dark one, deep on a light one; grey when idle.</summary>
internal readonly record struct Palette(Color Down, Color Up, Color Idle)
{
    internal static Palette Current() => LightTaskbar()
        ? new(Color.FromArgb(0x00, 0x7A, 0x33), Color.FromArgb(0xA8, 0x52, 0x00), Color.FromArgb(0x70, 0x70, 0x70))
        : new(Color.FromArgb(0x51, 0xFF, 0x70), Color.FromArgb(0xFF, 0xB4, 0x54), Color.FromArgb(0x9A, 0x9A, 0x9A));

    // The taskbar follows the Windows mode ("SystemUsesLightTheme"), not the app mode.
    static bool LightTaskbar()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
    }
}
