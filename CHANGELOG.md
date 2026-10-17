# Changelog

## 2.1.0 — 2026-10-17

First public release of **Visor** (formerly Display Profile Manager). It includes everything from 2.0.0 below, which was never published separately.

### Fixes
- Startup crash on first launch: the navigation handler fired while the main window was still loading (`Nav_Checked` → `MainTabs` null). New `XamlLoadGuardTests` guard every handler that can run during XAML load.

### Gamer toolkit
- **Optimize** is split into Overview · System · Graphics · Input · Network · Startup · Cleanup. New: Ultimate Performance plan (create + activate + undo), HAGS on/off, startup apps manager (disable without deleting, undo), cache cleanup (temp, DirectX/NVIDIA/AMD shader caches; irreversible but safe, sizes shown first).
- **Network** is split into Overview · Diagnostics · DNS · TCP. New: DNS server speed test (current, Cloudflare, Google, Quad9, AdGuard, OpenDNS, custom) and apply/undo, DNS cache flush, Nagle per adapter, multimedia network throttling, TCP auto-tuning restore, TCP info card. Texts say honestly what helps (DNS speeds up finding servers, not in-game ping; Nagle only matters for TCP games).
- Admin-only changes go through a per-action elevated helper (`Visor.exe --elevated`): one UAC prompt per change, allowlisted operations only, exact undo from the journal. Visor itself never runs elevated.
- UX pass: themed menus (no default white gutter, even 32-DIP rows, anchored under their button), scrollbars only while scrolling and outside the cards, aligned fields/buttons, layout rounding on every window, DPI-scaled tray menu. Details: `docs/UX-PASS-2026-10-17.md`.
- Fixes: audio switch to a device that no longer exists (validated, logged once, back-off); the "ultimate" power plan alias now resolves the installed scheme instead of the hidden template.

### Gaming Optimization Center (stages 1–5)
- New shell: nav rail Home · My PC · Games · Optimize · Network · Monitor · Settings (Ctrl+1..7), collapsible rail, Tooltip mode, Themes picker; pages are separate `UserControl`s (`Views\Pages`), `MainWindow.xaml` down to ~230 lines.
- My PC: hardware inventory (CPU, RAM modules, GPU driver, disks with SMART via `MSFT_PhysicalDisk`, board/BIOS, monitors) and 1 Hz sensors — CPU %, RAM, GPU temperature/utilization/clocks/throttle flags (NvAPI), disk and network throughput; health summary with honest thresholds; crash/WHEA/TDR events from the System log. CPU temperature is shown as unavailable (needs a kernel driver — none shipped).
- Optimize: reversible, journaled actions only — Game Mode, Xbox Game Bar background, mouse acceleration, fullscreen optimizations per game; "needs admin"/"managed by profile"/"manual" items are info-only; suggestions derived from measured facts (GPU throttling, low disk, old driver, crash events, RAM pressure) with why/risk/undo text and evidence.
- Network: live throughput, Wi-Fi (WlanAPI, SSID never logged), continuous ping series with p50/p95/jitter/loss, traceroute with per-hop stats, Diagnose (findings as hypotheses with evidence and manual suggestions), report export (identifiers off by default).
- Monitor: FPS / frame time / 1 % and 0.1 % lows / p99 / stutters / GPU-vs-CPU-bound estimate via Intel PresentMon run elevated on explicit request (UAC per capture); PresentMon is not bundled — the page explains where to get it; signature/pinned-hash verified before launch; optional FPS OSD on the game's monitor.
- Tests: 1036 (849 app + 187 installer).

### Stabilisation (stage 0)
Before the shell redesign (`docs/ANALYSIS-2026-10-13-v2.md` §4).

### Fixes (analysis §2.1–2.2)
- SafeAction (Game Mode, High performance): a failed power-plan switch is reported as a failure, Undo survives repeated clicks and an app restart, and Windows tweaks are blocked while a game session is active so they no longer fight the session snapshot.
- Home refreshes only while it is visible, not on every monitor event while hidden in the tray; the Ctrl+K palette cannot start a second game scan.
- System: the process sample no longer opens a handle to every process; "End process" is removed.
- Core: a power-plan change is tracked even when the read-back fails; a second Visor process (`--list`, `--emergency`) no longer overwrites the running instance's log.

### Code structure
- `MainWindow.xaml.cs` split into partial files per page (no behaviour change) so the Stage 1 shell work can proceed safely.

### Tests
- New `tests/Visor.Installer.Tests`: `PathGuard` (protected folders incl. case/trailing-slash variants, relative-path and manifest sanitising, known-file allowlist) and `InstallManifest` load/save. Compiles `Installer/Safety.cs` directly; nothing runs Setup.
- Always-passing tests fixed: session-snapshot tests now serialise with the service's real `JsonSerializerOptions`; machine-dependent cases skip explicitly instead of returning early.
- Packages: `Microsoft.NET.Test.Sdk` 18.11, `Xunit.SkippableFact` 1.5 (xunit stays 2.9); `System.Management` 10.0.12.

### Repository and docs
- `.gitignore` adds `TestResults/`, `*.trx`, `*.coverage`, `*.log` and Cursor scratch (`.cursor/*` except `rules/`); `.gitattributes` normalises line endings and marks `.png/.ico/.wav` binary.
- `build-release.ps1` runs `dotnet test Visor.sln -c Release` before publishing (`-SkipTests` to bypass) and warns when the git tree is dirty.
- README: accurate note on what the tests touch, data-file table lists `ui-state.json`, `active-session.json`, `history.json` and `profiles.json.bak`, screenshot note.
- `docs/AUDIT-2026-10-13.md` marked as a historical snapshot; QA checklist updated for the current shell and the SafeAction changes.

## 2.0.0 — Visor (not published separately)

Display Profile Manager is now **Visor**. Settings migrate automatically from `%AppData%\DisplayProfileManager` to `%AppData%\Visor` (the old folder is left untouched).

### Platform
- .NET 10 (was .NET 6, out of support). The app ships self-contained: no runtime install needed.
- New installer (.NET Framework 4.8, built into Windows 10/11): no admin rights, no runtime download, small `Uninstall.exe`, install manifest, safe upgrade with rollback, optional removal of the old Display Profile Manager (settings kept).
- PerMonitorV2 DPI awareness; version in one place (`Directory.Build.props`).

### Security
- Imported packs can no longer bring companions, launch tasks or exe paths; system process names are rejected.
- Executable paths: UNC, device and network paths are rejected, and symlinks/junctions are resolved before validation.
- QRes fallback from `C:\Tools` removed; logs no longer contain companion arguments or the user profile path.

### Reliability
- Exit, Emergency Restore and sign-out no longer reset resolution or power plan to stale install-time values.
- Windows shutdown/logoff is handled cleanly (no false "crash" restores, calibrated gamma LUTs are no longer wiped).
- One snapshot per game session; profile switches no longer overwrite the original desktop state.
- Only the settings a profile actually changed are restored (audio, notifications, Auto HDR, brightness, layout, FPS cap).
- Your own NVIDIA per-game FPS cap is no longer cleared; the original value is restored.
- Hotkeys never run heavy work inside the keyboard hook, fire exactly once, and keep working in games that block system hotkeys.
- Multi-monitor: the monitor that is actually changed is used for snapshot, gamma and driver color.
- Atomic config and snapshot writes, a backup that is never overwritten by a corrupt file, and tolerance for nulls in the config.
- Companions only track processes Visor started; your own instances are never killed.
- Failed restores are retried instead of being silently dropped. Resolution is re-applied if Windows reverts it mid-game.

### UI / UX
- New brand and icon, cyan → violet accent, full EN/RU localization.
- Fixed: empty resolution/FPS boxes, preset editor showing the wrong preset, the last preset not deletable.
- Confirmation plus Undo for destructive actions; disabled states, visible scrollbars and keyboard focus.
- Clear color sliders (Brightness / Contrast / Gamma / Vibrance / Hue / Shadows) and readable preset summaries.
- Setup wizard: OS language, a "scan my games" step, autostart off by default.
- In-game OSD banner on preset hotkeys, the overlay on the game's monitor, and shortcuts (Ctrl+1..6, Ctrl+K, Ctrl+F, Del).
- Games hub merges Play + Library: one list, sticky Apply/Launch, editor tabs Display · Color · Session · Companions.
- Home dashboard with live session/display/library status, command palette (Ctrl+K), and Tools → History (`history.json`).
- System page: read-only PC overview, process sample, SafeAction Game Mode + High performance power plan with undo.
- Network page: adapter overview, ICMP latency/loss/jitter probe (stoppable), DNS resolve timing (read-only).
- Home suggestions: recommendation engine over Visor / System / Network signals; Windows tweaks apply only via SafeAction.
- Shell fit helpers + tests for 100/125/150% DIP work areas; Settings default height fits 768-px @125%; command palette clamps to work area.
- Launch game: Steam titles start via `steam://rungameid` (AppID from `appmanifest_*.acf`), not by running the .exe alone.
- Faster release builds: ReadyToRun publish; game scan skips duplicate Steam/Epic trees; Steam AppID resolve is cached.
- Default preset hotkeys no longer need a NumPad (Ctrl+Alt+1..5).

### Performance
- Process polling no longer opens process handles or allocates per tick: a single `EnumProcesses` + kernel name query replaces `Process.GetProcessesByName` (≈6 ms → <0.1 ms per poll on a ~290-process machine). Polling stops entirely when no profile is enabled.
- GPU detection, driver baseline capture and WMI watchers no longer block the first paint.
- Log file is buffered and rotated at 2 MB (`Visor.log.1`), instead of growing without limit.
- Bounded shutdown: tray exit ≤ ~10 s, Windows sign-out ≤ ~3 s, with the display restore never skipped.

### Windows, layout and motion
- Every window is pulled fully into the current monitor's work area (DPI-aware, taskbar excluded) on open and on restore of a saved position; dialogs are owned and centered on their parent, or shown on the taskbar when the parent is hidden in the tray.
- Settings, theme and wizard default heights fit a 768-px screen at 125 % scaling (work area ~566 DIP); nested lists forward the mouse wheel at their edge.
- Windows "Show animations" setting is honoured: decorative fades and loops are skipped, functional feedback stays instant. Looping animations pause while a window is hidden.
- Accessibility names on icon-only buttons and hotkey fields; larger hit targets on the overlay pill.

### Tests
- New `tests/Visor.Tests` (xunit): path security, process-name matching, config creation/migration/recovery/atomic save, pack import sanitization, model cloning, hotkey gestures, display helpers, window-bounds clamping, ShellFit DPI targets, localization, activity history, command palette, SafeAction / System info, NetworkStats / adapter ranking, RecommendationEngine. `dotnet test Visor.sln`.

### Cleanup
- Removed dead code (`LightSysInfoService`, unused display/hotkey APIs, stubs) and merged duplicated logic (profile cloning, process-name normalization, display interop).
