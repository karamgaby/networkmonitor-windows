# NetworkMonitor for Windows

Live network speed in the Windows tray. It's an independent Windows take on
[NetworkMonitor](https://github.com/kevinabouhanna/NetworkMonitor) for macOS, built feature by
feature. It shares that app's goals, not its code.

The tray icon shows the download speed on the top row (green) and the upload speed on the
bottom row (amber). Each row is at most four characters: `850K`, `4.2M`, `120M`. Both rows turn
grey at `0` when nothing is moving. Hover over the icon for exact figures and the network name.
Double-click it to open Resource Monitor.

Right-click the icon for these options:

- **Units:** MB/s or Mbps.
- **Download colour…** and **Upload colour…**
- **Start at login**

Your choices are saved in `%APPDATA%\NetworkMonitor\settings.json`. If the file is damaged, the
app starts with the defaults.

The icon is redrawn when the taskbar's display scale changes, scaled up by whole pixels only, so
it stays sharp. At 200% it's drawn at double size. At 150% it's drawn at normal size in the middle
of the larger icon.

## Build, test, run

Needs the .NET 8 SDK.

```powershell
dotnet test                                    # 480 tests
dotnet run --project src\NetworkMonitor        # run from source
dotnet publish src\NetworkMonitor -c Release -o "$env:LOCALAPPDATA\Programs\NetworkMonitor"
```

The published copy is what **Start at login** (right-click the icon) registers, under
`HKCU\…\Run\NetworkMonitor`. Only one copy runs at a time.

Windows 11 puts new tray icons in the overflow (^). To keep this one visible, drag it onto the
taskbar, or turn it on in Settings → Personalization → Taskbar → Other system tray icons.

## What it counts

The app reads byte counters from `GetIfTable2` and counts only the **physical network cards
that are up**. Both conditions come from the row's own flags: `HardwareInterface` must be set
and `FilterInterface` must not be.

That counts every byte once:

- **Left out:** loopback, VPN tunnels (Tailscale, WireGuard, TAP) and Hyper-V/WSL `vEthernet`
  adapters. Their traffic also crosses the physical card, so counting them would count it twice.
- **Also left out:** the NDIS filter layers stacked on each card (WFP, QoS, Native WiFi). They
  report the card's traffic again. On a typical Wi-Fi card, counting them would multiply the
  speed by five.
- **Adapter changes:** a card that just came up waits one second for its baseline. A card that
  goes away, or whose counter resets, adds nothing. So plugging in Ethernet never shows a spike.

These are card totals, so home-network traffic (file copies, casting) is included.
Internet-only counting needs per-app data. That's step 2 below.

## Layout

| File | What it does |
|---|---|
| `InterfaceCounters.cs` | `GetIfTable2` P/Invoke; `MIB_IF_ROW2` layout, checked by a test |
| `RateMeter.cs` | Turns counter deltas into bytes/second, per interface |
| `SpeedFormat.cs` | `Compact` text for the icon, `Full` text for the tooltip |
| `PixelFont.cs`, `TrayIconRenderer.cs` | 3×5 pixel font, drawn sharp at 16 px and scaled by whole pixels at high DPI |
| `TrayApp.cs` | Tray icon, one-second timer, menu, light/dark taskbar colours |
| `StartupRegistration.cs` | Start at login |

## Roadmap

Each step maps one macOS feature to its Windows mechanism.

1. ✅ **Live speed in the tray:** `GetIfTable2`.
2. **Which apps used your data:** an elevated service reads `Microsoft-Windows-Kernel-Network`
   ETW events (per process: size and remote address) and stores the totals in SQLite. The tray app
   talks to the service over a named pipe. Dropping private, link-local and `100.64/10` remote
   addresses makes the counts internet-only. WSL and Hyper-V guest traffic is never attributed
   to a Windows process; it gets its own row, the card total minus everything attributed.
3. **History from before install:** import SRUM (`sru\SRUDB.dat`, the database behind Settings →
   Data usage), read from a VSS copy.
4. **Connections:** `GetExtendedTcpTable`/`GetExtendedUdpTable` with owning processes,
   plus an offline country and network database (DB-IP Lite or iptoasn).
5. **Blocking:** Windows Filtering Platform filters at `ALE_AUTH_CONNECT`, matched on app and
   remote address.
6. **Ask before connecting:** block by default, subscribe with `FwpmNetEventSubscribe0`, prompt,
   then add an allow filter. No kernel driver is needed.
7. **Ad and malware blocking:** a local DNS filter on `127.0.0.1`.
8. **Hotspot mode:** mark the phone's hotspot as a metered connection. Windows Update and the
   Store already pause on metered connections.

## License

[Apache 2.0](LICENSE).
