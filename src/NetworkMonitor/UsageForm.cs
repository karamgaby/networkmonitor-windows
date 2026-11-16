namespace NetworkMonitor;

/// <summary>Per-app totals for today, the last 7 days or this month, from the service, refreshed every 5 s.</summary>
internal sealed class UsageForm : Form
{
    readonly ComboBox _period = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    readonly Label _status = new() { AutoSize = true, Padding = new Padding(8, 6, 0, 0) };
    readonly Button _install = new() { Text = "Install usage service…", AutoSize = true, Visible = false };
    readonly ListView _list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        ShowItemToolTips = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };
    bool _busy;

    public UsageForm()
    {
        Text = "NetworkMonitor: app usage";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 460);
        StartPosition = FormStartPosition.CenterScreen;

        _period.Items.AddRange(["Today", "Last 7 days", "This month"]);
        _period.SelectedIndex = 0;
        _period.SelectedIndexChanged += async (_, _) => await Reload();

        _list.Columns.Add("App", 260);
        _list.Columns.Add("Downloaded", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Uploaded", 100, HorizontalAlignment.Right);
        _list.Columns.Add("Home network", 110, HorizontalAlignment.Right);

        _install.Click += async (_, _) =>
        {
            _install.Enabled = false;
            await Task.Run(() => Installer.Run(install: true));
            _install.Enabled = true;
            await Reload();
        };

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6) };
        top.Controls.AddRange([_period, _status, _install]);
        Controls.Add(_list);
        Controls.Add(top);

        _timer.Tick += async (_, _) => await Reload();
        Shown += async (_, _) => { await Reload(); _timer.Start(); };
    }

    async Task Reload()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            DateOnly from = _period.SelectedIndex switch
            {
                1 => today.AddDays(-6),
                2 => new DateOnly(today.Year, today.Month, 1),
                _ => today,
            };
            DayUsage? usage = await UsagePipe.Ask(UsagePipe.Name, new UsagePipe.Request(from, today));
            if (IsDisposed) return;
            if (usage is null)
            {
                _status.Text = "The usage service isn't running.";
                _install.Visible = true;
                _list.Items.Clear();
                return;
            }
            _install.Visible = false;
            _status.Text = $"Network card: ↓ {SpeedFormat.Amount(usage.CardDown)}   ↑ {SpeedFormat.Amount(usage.CardUp)}";
            Fill(usage);
        }
        finally
        {
            _busy = false;
        }
    }

    void Fill(DayUsage usage)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var (key, a) in usage.Apps.OrderByDescending(p => p.Value.Total))
        {
            _list.Items.Add(new ListViewItem([
                key.Contains('\\') ? Path.GetFileNameWithoutExtension(key) : key,
                SpeedFormat.Amount(a.Down), SpeedFormat.Amount(a.Up), SpeedFormat.Amount(a.LanDown + a.LanUp),
            ]) { ToolTipText = key });
        }
        // WSL and Hyper-V guests, packet headers, ARP/DHCP: on the card, but no Windows app sent them.
        long otherDown = usage.CardDown - usage.Apps.Values.Sum(a => a.Down + a.LanDown);
        long otherUp = usage.CardUp - usage.Apps.Values.Sum(a => a.Up + a.LanUp);
        if (otherDown > 0 || otherUp > 0)
        {
            _list.Items.Add(new ListViewItem([
                "Not matched to an app", SpeedFormat.Amount(Math.Max(0, otherDown)), SpeedFormat.Amount(Math.Max(0, otherUp)), "",
            ])
            {
                ForeColor = SystemColors.GrayText,
                ToolTipText = "Counted by the network card but not sent by a Windows app: WSL and Hyper-V guests, packet headers, ARP and DHCP.",
            });
        }
        _list.EndUpdate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
