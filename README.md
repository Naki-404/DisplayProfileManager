<p align="center">
  <img src="docs/screenshots/app-icon.png" width="96" alt="Visor">
</p>

<h1 align="center">Visor</h1>

<p align="center">
  <strong>Gaming optimization center for Windows.</strong><br>
  Per-game display profiles, PC and network diagnostics, reversible tweaks and FPS monitoring —<br>
  honest about what each setting really does, with one-click undo for every change.
</p>

<p align="center">
  <a href="https://github.com/Naki-404/DisplayProfileManager/releases/latest"><img src="https://img.shields.io/github/v/release/Naki-404/DisplayProfileManager?style=flat-square&label=release" alt="Latest release"></a>
  <a href="https://github.com/Naki-404/DisplayProfileManager/releases/latest"><img src="https://img.shields.io/github/downloads/Naki-404/DisplayProfileManager/total?style=flat-square" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-22D3EE?style=flat-square" alt="Windows 10/11 x64">
  <img src="https://img.shields.io/badge/version-2.2.0-8B5CF6?style=flat-square" alt="Version 2.2.0">
  <img src="https://img.shields.io/badge/lang-EN%20%2F%20RU-8B9AAB?style=flat-square" alt="English / Russian">
  <img src="https://img.shields.io/badge/tests-1500%2B-15803D?style=flat-square" alt="1500+ tests">
</p>

<p align="center">
  <a href="#download">Download</a> ·
  <a href="#what-visor-does">Features</a> ·
  <a href="#honest-by-design">Honest by design</a> ·
  <a href="#safety">Safety</a> ·
  <a href="#where-your-data-lives">Data</a> ·
  <a href="#build-from-source">Build</a>
</p>

> **Formerly Display Profile Manager.** Same app, new name and a lot more inside. Installing Visor offers
> to remove the old version; your profiles and settings are carried over automatically.

---

## Download

Get the latest build from **[Releases](https://github.com/Naki-404/DisplayProfileManager/releases/latest)**:

| File | What it is |
|------|------------|
| `Visor-Setup.exe` | **Recommended.** Per-user installer: no admin rights, nothing else to install. Start Menu shortcut + Apps & Features entry. |
| `Visor-<version>-portable.zip` | Portable build. Unzip anywhere and run `Visor.exe`. |
| `SHA256SUMS.txt` | Checksums to verify the downloads. |

Windows 10/11 x64. Both builds are self-contained (.NET included). The builds aren't code-signed yet, so
SmartScreen may show *"Windows protected your PC"* — click **More info → Run anyway**.

---

## What Visor does

| Section | What you get |
|---|---|
| **Home** | One dashboard: active game session, CPU / GPU / RAM / storage at a glance, health (temperatures, stability, drivers), network status, top suggestions. |
| **My PC** | Hardware inventory and live sensors: CPU load, RAM, GPU temperature / load / clocks / throttling (NVIDIA), disks with SMART health, crash and hardware-error events from the Windows log. |
| **Games** | Per-game profiles: resolution, refresh rate, power plan, color, FPS cap, companions. Applied when the game starts, **restored when it exits**. Hotkey color presets, pack gallery, Steam launch. |
| **Optimize** | Split into **Overview · System · Graphics · Input · Network · Startup · Cleanup**. Game Mode, Xbox Game Bar background capture, mouse acceleration, fullscreen optimizations per game, Ultimate Performance plan, hardware-accelerated GPU scheduling, startup apps manager, shader/temp cache cleanup — each with *why / risk / how to undo*. |
| **Network** | Split into **Overview · Diagnostics · DNS · TCP**. Live throughput, Wi-Fi signal, continuous ping with p50/p95/jitter/loss, traceroute, one-click **Diagnose** that explains *where* the problem starts (Wi-Fi, LAN, ISP, DNS), report export. DNS speed test across Cloudflare / Google / Quad9 / AdGuard / OpenDNS / custom with apply & undo, DNS cache flush, Nagle, network throttling, TCP auto-tuning. |
| **Monitor** | FPS, frame time, 1 % / 0.1 % lows, p99, stutters and a *likely* GPU / CPU / capped estimate from Visor's **built-in frame capture** (Windows ETW events — nothing to download); optional FPS overlay on the game's monitor. |
| **Settings** | Defaults, app hotkeys, history, log, themes (dark / light / custom), English / Russian. |

Also: tray mode, in-game overlay with live color controls, center zoom (Windows Magnifier), command
palette (**Ctrl+K**), section shortcuts (**Ctrl+1…7**), emergency display reset.

---

## Honest by design

- **No fake boosts.** Visor never promises "+30 % FPS". Suggestions come from measured facts (GPU
  throttling reported by the driver, disk space, startup items, event-log errors) and show the evidence.
- **Plain-language trade-offs.** Faster DNS speeds up finding servers and launchers — it doesn't lower
  in-game ping. Turning off Nagle helps only TCP-based games (MMOs); most shooters use UDP. Network
  throttling matters mainly when music or a stream plays during the game. Visor says so on every card.
- **Measure, don't guess.** Use **Monitor** (built-in frame capture) to compare FPS and frame times before and after a change.
- **Unavailable means unavailable.** CPU temperature needs a kernel driver, which Visor doesn't ship — it
  shows "n/a" instead of a made-up number.

---

## Safety

- **Every Windows change can be undone.** Visor journals the exact previous value (including "this value
  didn't exist") and restores it — and warns you if something else changed the setting in between.
- **Visor never runs as administrator.** Changes that need admin rights (DNS, TCP, HKLM, HAGS) go through a
  tiny elevated helper: **one UAC prompt per change**, and it only accepts a fixed allowlist of operations.
- **No debloat, no registry "cleaning", no disabling services, no killing processes.** Startup apps are
  disabled (like Task Manager does), never deleted. Cache cleanup shows sizes first and asks before deleting.
- **Games are left alone.** Visor never touches game memory or files and never injects into a game.
  It changes only system and driver settings. In-game hotkeys use standard low-level keyboard/mouse hooks
  inside Visor's own process (like Discord or OBS). FPS capture reads Windows' own frame events (ETW)
  through a small helper built into `Visor.exe` — no hooks into the game, no injection, no external tool.
  Still, follow your game's rules.
- **No telemetry, no accounts, no cloud.** Network probes run only when you click.

### FPS monitoring

The frame capture is part of Visor — there is nothing to download or install. A real-time ETW session
needs administrator rights, so pressing **Start capture** shows **one UAC prompt** and starts a small
helper (`Visor.exe --frame-capture`); Visor itself stays unelevated. **Stop** needs no second prompt, and
the helper also ends on its own when the game or Visor exits, or after an hour.

Measured: FPS, frame time (present-to-present), 1 % / 0.1 % lows, p99, stutters, and a *likely* GPU-bound /
CPU-bound / capped label with the numbers behind it (GPU load is read on NVIDIA only for now). **Not measured
yet:** displayed vs dropped frames, display latency and click-to-photon. Details:
[docs/MONITOR-NATIVE.md](docs/MONITOR-NATIVE.md).

---

## How a game session works

```text
  game.exe starts  ->  matching profile  ->  snapshot desktop state  ->  apply resolution / power / color / extras
                                                                                       |
  game.exe exits   <-------------------------------------------------------------------+
                   restore exactly what the profile changed (also after a crash or sign-out)
```

1. Add or scan your games in **Games** (Steam, Epic and other libraries are detected).
2. Tune color, presets and session options per game.
3. Leave Visor in the tray. Autostart is optional.
4. Play — profiles apply and restore on their own.

---

## Where your data lives

| Path | Purpose |
|------|---------|
| `%LocalAppData%\Programs\Visor\` | Default install folder (`Visor.exe`, `Assets\`, `Uninstall.exe`) |
| `%AppData%\Visor\profiles.json` | Profiles and settings (`.bak` = last known-good copy) |
| `%AppData%\Visor\active-session.json` | Desktop state captured at game start; deleted after a successful restore |
| `%AppData%\Visor\safeactions.json` | Undo journal for Optimize / Network tweaks |
| `%AppData%\Visor\history.json` | Recent activity |
| `%AppData%\Visor\ui-state.json` | Window position and other UI state |
| `%AppData%\Visor\Visor.log` | App log (rotated at 2 MB, user paths masked) |

Upgrading from Display Profile Manager imports `%AppData%\DisplayProfileManager\` on first start; the old
folder is never deleted.

### Installer

- Per-user, no UAC. A custom folder always gets a `Visor` subfolder.
- Safe updates: Visor is closed first (after asking), files are staged and swapped, and a failed update
  rolls back. Uninstall removes only the files listed in `.visor-install.json`.
- Silent: `Visor-Setup.exe /silent [/dir="D:\Apps"]` · `"%LocalAppData%\Programs\Visor\Uninstall.exe" /uninstall /silent`
- Exit codes: `0` success, `1` failure, `2` nothing to uninstall.

---

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) on Windows.

```powershell
git clone https://github.com/Naki-404/DisplayProfileManager.git
cd DisplayProfileManager
dotnet build .\Visor.sln -c Release
dotnet test .\Visor.sln
```

Tests never change display, power, registry or network settings; they write only to temp folders.
Release artifacts (tests run first):

```powershell
powershell -ExecutionPolicy Bypass -File .\build-release.ps1
```

```text
Visor.sln                       app + installer + tests
Visor.csproj                    app (WPF, net10.0-windows)
Views/ Controls/                shell, pages (Home, My PC, Games, Optimize, Network, Monitor, Settings)
Services/                       display, sessions, hardware, network, monitor, optimize, elevation
Installer/                      Visor-Setup.exe / Uninstall.exe (WPF, net48)
tests/                          xunit suites (app + installer)
docs/                           design notes, tweak docs, QA checklist
build-release.ps1               release pipeline · version lives in Directory.Build.props
```

Docs: [Network tweaks](docs/NETWORK-TWEAKS.md) · [PC tweaks](docs/PC-TWEAKS.md) ·
[Hardware sensors](docs/HARDWARE-SENSORS.md) · [FPS monitor](docs/MONITOR-NATIVE.md) ·
[QA checklist](docs/QA-CHECKLIST.md) · [Changelog](CHANGELOG.md)

---

## Author

**Nakidev** — Discord & Telegram [@nakidev](https://t.me/nakidev) · [GitHub](https://github.com/Naki-404)
