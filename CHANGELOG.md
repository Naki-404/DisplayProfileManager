# Changelog

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
- In-game OSD banner on preset hotkeys, the overlay on the game's monitor, and shortcuts (Ctrl+1..4, Ctrl+F, Del).
- Default preset hotkeys no longer need a NumPad (Ctrl+Alt+1..5).

### Cleanup
- Removed dead code (`LightSysInfoService`, unused display/hotkey APIs, stubs) and merged duplicated logic (profile cloning, process-name normalization, display interop).
