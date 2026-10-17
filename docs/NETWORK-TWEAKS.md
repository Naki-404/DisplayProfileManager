# Network tweaks and the per-action elevated helper

Visor never runs elevated. A handful of system tweaks (HKLM values, adapter DNS, TCP globals) need
administrator rights. They go through a **short-lived elevated helper**: one UAC prompt per
apply or undo, a fixed allowlist, every change journaled with an exact undo, and nothing applied
without a click.

Code: `Services/System/Elevation/*` (helper, broker, allowlist) and `Services/Network/Tweaks/*`
(namespace `Visor.Services.NetworkTweaks`).

## 1. Elevated helper

### Flow

```
UI click ─► SafeActionService.ApplyAsync(id)
              │ read previous state (no admin: HKLM read / netsh show / NIC props)
              │ NoOp if already at target
              ▼
            target.WriteStateAsync(state) ─► ElevationBroker.RunAsync(ops)
              │  1. allowlist check (an invalid batch never shows a UAC prompt)
              │  2. %LocalAppData%\Visor\elevation\<id>.req.json   (folder ACL: user, Administrators, SYSTEM)
              │  3. ShellExecute runas: Visor.exe --elevated <id>.req.json <id>.res.json   (hidden, 60 s timeout)
              │       └─ ElevatedHelperHost.Run: elevated? → path pair ok? → parse → validate ALL ops → execute → <id>.res.json
              │  4. read result, delete both files
              ▼
            re-read real state ─► Applied (journal: exact previous + verified target) / Failed (code)
```

- `--elevated` is handled first in `App.TryHandleCliArgs`, before the log, mutex, config, hooks,
  services or any window exist. The helper touches none of them (it does not even write the log —
  the unelevated broker logs the result). Exit code 0 = every op succeeded, 1 = anything else.
- UAC "No" (Win32 error 1223) → `ElevationOutcome.Declined` → `SafeActionCodes.ElevationDeclined`.
  Nothing changes, nothing is journaled.
- The helper is stateless. Undo data is captured by Visor **before** the batch and journaled by
  `SafeActionService` (`safeactions.json`).
- Ops in a batch are independent; the helper runs all of them and reports each. Visor re-reads the
  real state afterwards, so a partial batch is still journaled ("moved") and undoable.
- Timeout: after 60 s the broker reports `Failed/timeout` and deletes the request. A helper that
  starts later finds no request and does nothing; one that was already running may still finish —
  the verify re-read catches that, and the change is journaled like any other.

### Why an allowlist

The helper runs with full admin rights and takes input from a file the unelevated user can write.
Without a fixed list it would be a "run anything elevated" gadget for any process of the same user.
`ElevationAllowlist` allows only:

| Kind | Target | Names / values |
|---|---|---|
| `RegSetDword`, `RegDeleteValue` | `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID}` (exact braced GUID, key must already exist) | `TcpAckFrequency`, `TCPNoDelay`, `TcpDelAckTicks` |
| `RegSetDword`, `RegDeleteValue` | `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile` | `NetworkThrottlingIndex`, `SystemResponsiveness` |
| `RegSetDword`, `RegDeleteValue` | `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers` | `HwSchMode` |
| `RegSetString`, `RegDeleteValue` | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` | any non-empty name without control characters; data ≤ 2048 chars, no NUL |
| `RegSetBinary`, `RegDeleteValue` | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run` | any safe name; hex data ≤ 64 bytes |
| `DnsSetStatic` | interface GUID of an **existing** adapter | 1–4 canonical, distinct IPv4 addresses (no 0.x, multicast, reserved, broadcast; loopback and private ranges are allowed so undo can restore a local DNS proxy) |
| `DnsSetDhcp` | interface GUID of an existing adapter | no value |
| `TcpGlobalSet` | `autotuninglevel` | `normal`, `disabled`, `restricted`, `highlyrestricted` (**not** `experimental`) |
| `TcpGlobalSet` | `ecncapability` / `rss` | `enabled`/`disabled`/`default` / `enabled`/`disabled` |
| `PowerDuplicateScheme` | — | only `e9a42b02-d5df-448d-aa00-03f14749eb61` (Ultimate Performance template) |

Registry paths are HKLM-relative and checked by **shape**, never normalised: a hive prefix, `..`,
`/`, `:`, empty or padded segments or control characters reject the op instead of being "repaired".
DWORD data is decimal (unsigned, or the signed journal form such as `-1` for `0xFFFFFFFF`). Unknown
op kinds, more than 32 ops, a `Name` on DNS / TCP / power ops: rejected. **One bad op rejects the whole
batch before anything runs** (`ElevationOutcome.Rejected`, per-op reasons). The broker runs the same
check before launching, so a batch the helper would refuse never costs a UAC prompt.

Execution (`SystemElevatedExecutor`): registry through the 64-bit HKLM view; DNS through WMI
`Win32_NetworkAdapterConfiguration.SetDNSServerSearchOrder` (a list = static, `null` = back to
DHCP — one API for both directions, no adapter-name quoting as with netsh); TCP via
`%SystemRoot%\System32\netsh.exe int tcp set global k=v` with an argument list and a 20 s timeout;
power via `powercfg.exe /duplicatescheme <guid>` (the new GUID is parsed from the localized output
and returned in `ElevatedOpResult.Output`). Tools are always started by full System32 path.

### Why file based (no stdin / pipes)

A `runas` (ShellExecute) child cannot inherit redirected handles, so stdin/stdout to an elevated
process is not available without a named pipe — and a pipe server in the unelevated process would
be a new attack surface (anyone could connect / squat the name). Files in a per-user folder with an
explicit ACL are simple, auditable and short-lived:

- folder `%LocalAppData%\Visor\elevation`, inheritance off, Full Control for the current user,
  Administrators and SYSTEM only (Administrators so over-the-shoulder elevation with another admin
  account still works); stale files older than an hour are cleaned up;
- the helper accepts only a fully qualified `<id>.req.json` / `<id>.res.json` pair with the same
  32-hex id, in a folder named `…\Visor\elevation` that is not a junction / symlink; the request id
  inside the JSON must match; requests are ≤ 64 KB;
- the result is written to a fresh temp file (`CreateNew`) and renamed into place **without
  overwrite**, so a file or link planted at the result path makes the write fail instead of
  redirecting an elevated write;
- both files are deleted by the broker afterwards.

### Threat model

Same-user: anything running as the same unelevated user can already write the request folder and
could even start `Visor.exe --elevated` itself. What it cannot do is get more than the allowlist:
the worst case is "a value from the table above is set after the user approved a UAC prompt for
Visor". Other non-admin users cannot touch the folder (ACL). A portable Visor in a user-writable
folder is subject to the usual "elevating an exe from a user folder" caveat (DLL planting by the same
user); the installed build in Program Files is not. The helper never reads Visor's config or
profiles, never loads plugins, and never runs anything but the fixed System32 tools.

## 2. Network tweaks

All targets: `RequiresElevation = true`, apply / undo **only** via `SafeActionService.ApplyAsync` /
`UndoAsync` (the sync `Apply` / `Undo` refuse them with `needsElevation`, so a UAC prompt can never
block the UI thread). Previous state is read without admin rights; each write is verified by
re-reading. Undo refuses with `conflict` when the value was changed outside Visor (the user can
confirm and force it).

Wiring: `SafeActionService.CreateDefault(dir, setPlan, session, elevation: new ElevationBroker())`
registers `mmcss.net` and `tcp.autotuning.normal` and resolves `dns.set|…` / `tcp.nagle.off|…`
(also after a restart, from the journal). Without `elevation` nothing changes for existing callers.
The primary adapter is `NetworkTweakCatalog.PrimaryInterfaceGuid()` (adapter owning the default route).

### DNS — `dns.set|{GUID}|a.b.c.d,e.f.g.h` (undo slot `dns|{GUID}`)

- **What it really does**: changes which server answers name lookups. Faster DNS shortens the
  *first* connection to a host (launcher, store, matchmaking, web) by the lookup time; it does
  **not** change in-game ping — the game talks to its server by IP once connected. Privacy /
  filtering (Quad9 malware block, AdGuard ad block) are often the better reasons.
- **Previous state**: the adapter's `NameServer` value under
  `Tcpip\Parameters\Interfaces\{GUID}` — empty / absent = DHCP, otherwise the exact ordered static
  list. A value that cannot be restored exactly (non-IPv4 token, > 4 servers, wrong kind) makes the
  target *unavailable* rather than risking a lossy undo. `DnsSafeActionTarget.EffectiveDnsServers`
  (`NetworkInterface.GetIPProperties().DnsAddresses`) is what Windows uses now, for display and the
  benchmark baseline.
- **Apply / undo**: WMI `SetDNSServerSearchOrder` (list or DHCP), then the Windows DNS cache is
  flushed. Switching twice (Cloudflare → Google) keeps the **original** pre-Visor state as the undo
  point; switching back to the original clears the slot.
- **Scope**: IPv4 only. IPv6 DNS servers (often advertised by the router) are untouched and Windows
  may still use them — shown honestly in the UI text.
- `DnsCache.Flush()` (`dnsapi!DnsFlushResolverCache`, no admin) is a plain action, nothing to undo.

**Benchmark** (`DnsBenchmarkService`, no admin): minimal UDP DNS client (`DnsWire` + `UdpDnsQueryClient`,
random id, strays ignored, 1.5 s timeout, no third-party packages) sends A queries for 14 popular
game / store / CDN / platform names straight to each candidate's **primary** server (the Windows
cache is bypassed). Candidates: the current DNS, Cloudflare, Google, Quad9, AdGuard, OpenDNS and an
optional custom pair. One warm-up round is discarded (so the numbers reflect resolver-cache hits for
popular names, which is what you get in practice), then 3 measured rounds; in every (round, name)
step all candidates are queried in parallel so they share network conditions. Per candidate:
median, p95 (nearest rank), min, failure %. NOERROR, NXDOMAIN and truncated answers count as
answered; SERVFAIL / REFUSED / timeouts / malformed as failures. Ranking: reliable (≤ 5 % failures)
before unreliable before dead, then median, p95, failures; ties keep the current DNS first.
`SwitchRecommended` only when the winner is ≥ 5 ms **and** ≥ 20 % faster on the median, or the
current DNS drops > 5 % of queries while the winner drops ≤ 1 %.

### Nagle per adapter — `tcp.nagle.off|{GUID}`

- `TcpAckFrequency=1`, `TCPNoDelay=1` on the primary adapter's interface key.
- **Honest scope**: affects only TCP traffic (MMOs such as World of Warcraft, some MOBA / launcher /
  chat traffic). Most shooters and competitive games use UDP — no effect there. Applies to new
  connections (reconnect / restart the game).
- Undo restores both values exactly (deletes them when they were absent).

### Network throttling — `mmcss.net`

- `NetworkThrottlingIndex = 0xFFFFFFFF` (off; default 10) and `SystemResponsiveness = 10`
  (default 20; Windows treats 0–10 as 10, a lower existing value is kept).
- **Honest scope**: largely folklore. Windows throttles non-multimedia network processing only while
  MMCSS-scheduled audio / video plays, so a difference is plausible only with music, a stream or
  voice playing during the game, and is often not measurable. Full effect after a reboot.
- Undo restores both values exactly.

### TCP globals — info card + `tcp.autotuning.normal`

- `NetshTcpGlobalReader` runs `netsh int tcp show global` (no admin). netsh is **localized** and
  writes in the OEM code page; `NetshTcpGlobalParser` identifies fields by EN / RU label keywords,
  then by value tokens and line order (the auto-tuning line is the only one with
  normal / restricted / highlyrestricted / experimental; RSS is the line before it, ECN two after it),
  normalizes RU value words, and leaves anything else as `unknown` — never a guess.
- Action only when auto-tuning is `disabled` / `restricted` / `highlyrestricted` (typical leftovers of
  old "tweak guides" that cap the receive window and can cut download speed): set back to Windows'
  default `normal`. Undo sets the previous level. `experimental` / unreadable → unavailable (undo
  could not restore it within the allowlist).
- ECN and RSS: information only (state + explanation). RSS should normally be enabled; ECN is a
  matter of network equipment — no measurable gaming effect either way.

## 3. Limitations

- One UAC prompt per apply / undo; batching several tweaks into one prompt is possible with the
  broker but not exposed by `SafeActionService` (one target = one batch).
- DNS: IPv4 only, primary server timed, no DoH/DoT. The benchmark measures from this PC at this
  moment; results vary by time of day and ISP routing.
- Netsh parsing for locales other than EN / RU relies on value tokens and line order.
- The helper's adapter check uses the adapters visible at run time; a disabled adapter (which
  Windows does not list) is reported as unknown.
- Portable builds elevate an exe in a user folder (see threat model).
