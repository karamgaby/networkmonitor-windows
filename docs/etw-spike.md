# Step 2 test: per-app bytes from Kernel-Network ETW

`spikes/EtwSpike` is a real-time session on `Microsoft-Windows-Kernel-Network`
(`{7DD42A49-5329-4832-8DFD-43D979153A88}`, keywords IPv4 `0x10` and IPv6 `0x20`). It uses raw
`StartTrace`/`OpenTrace`/`ProcessTrace` calls, without TraceEvent. It ran on 2026-11-16 on Windows
11 build 26300, Wi-Fi 7 card, x64. Run it with:

```powershell
dotnet build spikes\EtwSpike -c Release
spikes\EtwSpike\bin\Release\net8.0-windows\EtwSpike.exe --seconds 30 [--loopback-load] [--log file]
```

## Results

**1. Without admin rights: not possible.** `StartTrace` fails with error 5 (access denied) for a
normal account. *Performance Log Users* doesn't help either. A fresh sign-in through
`runas` had the group in its token (`S-1-5-32-559`, checked by the program), and it still got
error 5. The ETW security descriptors are the Windows defaults
(`HKLM\SYSTEM\CurrentControlSet\Control\WMI\Security` has no entries). So on this build, that
group can't start a real-time session for this provider. **Step 2 needs a service with admin rights**,
with the tray app connecting to it over a named pipe.

**2. CPU** (the test program's own CPU, with a lock and a dictionary per event; it isn't optimised):

| Load | Events/s | CPU, one core | Events lost |
|---|---|---|---|
| Normal use (browser, Claude, node) | ~900 | 0.68% | 0 |
| Loopback TCP flood, ~850 MB/s | ~14,300 | 2.81% | 0 |

The cost follows the number of events, not the bytes. Loopback sends one event per 64 KB write.
Real network traffic sends one event per receive, which is smaller. A fast fibre link could reach
tens of thousands of events per second. So the step 2 callback should only add to counters, and
do the rest outside the event thread.

**3. Accuracy:**
- **Overall:** over 30 s, the ETW bytes (loopback excluded) came to **93%** of the network card's
  counters under load, and **71%** with light traffic.
- **Light traffic:** the card also counts packet headers, ACKs, ARP and DHCP, while ETW counts
  only the data each app sent or received. The gap mostly closes during downloads.
- **5-second windows:** these swing between 44% and 300%, because ETW delivers events up to
  1 s late (FlushTimer) and the card counters don't. Compare totals over a minute or more.

## The event layout, as decoded by TDH

All eight events start the same way: `PID:UInt32 size:UInt32 daddr saddr dport:UInt16 sport:UInt16 …`.
`daddr` and `saddr` are UInt32 for IPv4 and 16 bytes for IPv6.

| Event | Meaning |
|---|---|
| 10 / 11 | TCPv4 send / receive (10 also has `startime` and `endtime`) |
| 26 / 27 | TCPv6 send / receive |
| 42 / 43 | UDPv4 send / receive |
| 58 / 59 | UDPv6 send / receive |

- **Use the payload `PID`, not the event header's process ID.** On receive events the header
  belongs to whichever process happened to be running (`11: PID 41688, header PID 4448`).
- **`daddr` is the remote address** in both directions.

## What this changes for step 2

- **Record process names when the event arrives.** Many short-lived processes (curl, git, build
  tools) had already exited by the 5-second report and showed up as "(exited)". Look the name up
  on the first event from each PID, or subscribe to `Microsoft-Windows-Kernel-Process` start events.
- **Count traffic through a VPN tunnel once.** With a full-tunnel VPN (WireGuard), an app's bytes
  are attributed to it with a public `daddr`, and the VPN client then sends the same bytes again,
  encrypted, to its server. `saddr` is the local address, so it shows which interface carried the
  traffic, through `GetUnicastIpAddressTable`. Use it to show the app's bytes and leave the
  tunnel's copy out.
- **Keep loopback and LAN apart from internet traffic.** Loopback was by far the largest category
  (0.9–5.7 MB/s on an idle desktop, between node and Chrome). Anything to `100.64/10` is Tailscale,
  which crosses the card encrypted and is attributed to `tailscaled`.
