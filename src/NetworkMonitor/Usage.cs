using System.Globalization;
using System.Text.Json;

namespace NetworkMonitor;

/// <summary>One app's bytes. Down/Up are internet traffic; LanDown/LanUp are the home network. Loopback isn't kept.</summary>
internal sealed class AppUsage
{
    public long Down { get; set; }
    public long Up { get; set; }
    public long LanDown { get; set; }
    public long LanUp { get; set; }

    internal long Total => Down + Up + LanDown + LanUp;

    internal void Add(bool lan, bool send, long bytes)
    {
        if (lan) { if (send) LanUp += bytes; else LanDown += bytes; }
        else { if (send) Up += bytes; else Down += bytes; }
    }

    internal void Add(AppUsage o)
    {
        Down += o.Down; Up += o.Up; LanDown += o.LanDown; LanUp += o.LanUp;
    }
}

/// <summary>
/// One day's usage, or the sum of several days. Apps are keyed by exe path, or "System".
/// CardDown/CardUp are the physical network cards' own counters, the same ones the tray icon shows.
/// </summary>
internal sealed class DayUsage
{
    internal const string SystemApp = "System";
    internal const string Exited = "(exited before it could be named)";

    public long CardDown { get; set; }
    public long CardUp { get; set; }
    public Dictionary<string, AppUsage> Apps { get; set; } = new();

    internal AppUsage App(string key)
    {
        if (!Apps.TryGetValue(key, out var a)) Apps[key] = a = new AppUsage();
        return a;
    }

    internal void Add(DayUsage o)
    {
        CardDown += o.CardDown;
        CardUp += o.CardUp;
        foreach (var (key, a) in o.Apps) App(key).Add(a);
    }

    internal static string PathFor(string folder, DateOnly day) =>
        Path.Combine(folder, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".json");

    /// <summary>A missing file is an empty day. A damaged one is null, so the caller can keep it aside instead of overwriting it.</summary>
    internal static DayUsage? TryLoad(string path)
    {
        try
        {
            var day = JsonSerializer.Deserialize<DayUsage>(File.ReadAllText(path));
            if (day is null || day.Apps is null || day.Apps.Values.Any(a => a is null)) return null;
            return day;
        }
        catch (FileNotFoundException) { return new DayUsage(); }
        catch (DirectoryNotFoundException) { return new DayUsage(); }
        catch (JsonException) { return null; }
    }

    internal void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(this));
        File.Move(temp, path, overwrite: true); // a crash mid-write leaves the old file whole
    }
}

/// <summary>
/// The service's running totals. ETW events are added to a short window under a lock, and
/// <see cref="Flush"/> folds the window into the day, every 10 s. Thread-safe.
/// </summary>
internal sealed class UsageBook
{
    readonly string _folder;
    readonly object _windowGate = new(), _dayGate = new();
    Dictionary<string, AppUsage> _window = new();
    DateOnly _day;
    DayUsage _today;

    internal UsageBook(string folder, DateOnly today)
    {
        _folder = folder;
        _day = today;
        _today = LoadForWriting(today);
    }

    internal void Add(string app, bool lan, bool send, long bytes)
    {
        lock (_windowGate)
        {
            if (!_window.TryGetValue(app, out var a)) _window[app] = a = new AppUsage();
            a.Add(lan, send, bytes);
        }
    }

    /// <summary>Moves the window into <paramref name="day"/>. A new day saves the old one first.</summary>
    internal void Flush(DateOnly day, long cardDown, long cardUp)
    {
        Dictionary<string, AppUsage> window;
        lock (_windowGate) (window, _window) = (_window, new());
        lock (_dayGate)
        {
            if (day != _day)
            {
                _today.Save(DayUsage.PathFor(_folder, _day));
                _day = day;
                _today = LoadForWriting(day);
            }
            _today.CardDown += cardDown;
            _today.CardUp += cardUp;
            foreach (var (app, a) in window) _today.App(app).Add(a);
        }
    }

    internal void Save()
    {
        lock (_dayGate) _today.Save(DayUsage.PathFor(_folder, _day));
    }

    /// <summary>The sum of the days from <paramref name="from"/> to <paramref name="to"/>, today's from memory.</summary>
    internal DayUsage Read(DateOnly from, DateOnly to)
    {
        var total = new DayUsage();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            lock (_dayGate)
            {
                if (d == _day)
                {
                    total.Add(_today);
                    continue;
                }
            }
            if (DayUsage.TryLoad(DayUsage.PathFor(_folder, d)) is { } day) total.Add(day);
        }
        return total;
    }

    DayUsage LoadForWriting(DateOnly day)
    {
        string path = DayUsage.PathFor(_folder, day);
        if (DayUsage.TryLoad(path) is { } loaded) return loaded;
        File.Move(path, path + ".damaged", overwrite: true); // kept for a look, never overwritten by the new totals
        return new DayUsage();
    }
}
