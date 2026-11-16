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
- **App usage…:** how much each app downloaded and uploaded today, in the last 7 days, or this
  month. See [App usage](#app-usage).
- **Start at login**

Your choices are saved in `%APPDATA%\NetworkMonitor\settings.json`. If the file is damaged, the
app starts with the defaults.

The icon is redrawn when the taskbar's display scale changes, scaled up by whole pixels only, so
it stays sharp. At 200% it's drawn at double size. At 150% it's drawn at normal size in the middle
of the larger icon.

## Build, test, run

Needs the .NET 8 SDK.

```powershell
dotnet test                                    # 1012 tests
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

## App usage

Per-app totals come from `Microsoft-Windows-Kernel-Network` ETW events. Only an admin can start
a session for that provider; even *Performance Log Users* gets error 5 (see
[docs/etw-spike.md](docs/etw-spike.md)). So a small service does the counting, and the tray app
asks it for the totals:

```powershell
NetworkMonitor.exe --install-service     # asks for admin rights once
NetworkMonitor.exe --uninstall-service
```

The **Install usage service…** button in the App usage window does the same thing.

- **Where it runs:** the installer copies the app to `C:\Program Files\NetworkMonitor` and
  registers the **Network Monitor** service there. It runs as LocalSystem, starts
  automatically, and restarts after a crash. It never runs as SYSTEM from your user profile,
  where any program running as you could replace the exe.
- **What it keeps:** one JSON file per day in `%ProgramData%\NetworkMonitor\usage`, saved every
  minute. Apps are keyed by exe path. The folder is owned by Administrators, and only SYSTEM
  and Administrators can write to it. `service.log` is kept there too. Uninstalling keeps the
  history.
- **How the tray asks:** over the named pipe `\\.\pipe\NetworkMonitor`, one JSON line each
  way. Only people signed in at this PC can connect; network logons can't.
- **What counts:** each event's remote address decides. Loopback is dropped. Private,
  link-local, multicast and `100.64/10` (Tailscale) addresses go in the *Home network* column.
  Everything else is internet. The window also shows the network card's own total, and a *Not
  matched to an app* row for the difference: WSL and Hyper-V guests, packet headers, ARP and
  DHCP.
- **Cost:** about 0.6% of one core and 42 MB in normal use. A 20 MB test download was counted as
  20.03 MB for `curl.exe`, against 20.85 MB on the card.

Known limits:

- **Services** show up as `svchost.exe`, not split by service.
- **VPN traffic is counted twice:** once for the app, through the tunnel, and once for the VPN
  client, encrypted, on its way to the VPN server. The card total counts it once.
- **The last 10 seconds** before a crash are lost. A clean stop or shutdown saves them.

## Layout

| File | What it does |
|---|---|
| `InterfaceCounters.cs` | `GetIfTable2` P/Invoke; `MIB_IF_ROW2` layout, checked by a test |
| `RateMeter.cs` | Turns counter deltas into bytes/second, per interface |
| `SpeedFormat.cs` | `Compact` text for the icon, `Full` text for the tooltip |
| `PixelFont.cs`, `TrayIconRenderer.cs` | 3×5 pixel font, drawn sharp at 16 px and scaled by whole pixels at high DPI |
| `TrayApp.cs` | Tray icon, one-second timer, menu, light/dark taskbar colours |
| `StartupRegistration.cs` | Start at login |
| `Etw.cs` | Real-time ETW session: raw `StartTrace`/`OpenTrace`/`ProcessTrace` calls, x64 layouts |
| `AddressScope.cs` | Remote address → internet, home network or loopback |
| `Usage.cs` | Per-app and per-day totals, daily JSON files, and the 10 s window events go into |
| `UsageCollector.cs` | The service's work: ETW events → totals, exe paths by PID, the pipe |
| `UsagePipe.cs` | Named pipe server and client, with its access list |
| `WindowsService.cs` | Runs as a service through the SCM's own calls; `service.log` |
| `Installer.cs` | `--install-service` / `--uninstall-service`: copies to Program Files, locks down the data folder |
| `UsageForm.cs` | The App usage window |

## Roadmap

Each step maps one macOS feature to its Windows mechanism.

1. ✅ **Live speed in the tray:** `GetIfTable2`.
2. ✅ **Which apps used your data:** an elevated service reads `Microsoft-Windows-Kernel-Network`
   ETW events and keeps daily per-app totals. The tray app asks it over a named pipe. See
   [App usage](#app-usage).
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
