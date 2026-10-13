# Changelog

## Unreleased (2.1.0-dev)

Stage 0 of the 2.1 plan (`docs/ANALYSIS-2026-10-13-v2.md` §4): stabilise before the shell redesign. No new user-facing features; the Home / Games / Presets / System / Network / Tools layout is unchanged until Stage 1.

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

## 2.0.0 — Visor

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
