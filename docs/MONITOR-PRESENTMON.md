# FPS monitor — PresentMon integration (Этап 5)

Visor's FPS / frametime monitor does not hook, inject into or overlay games. It drives Intel's
**PresentMon** (MIT, https://github.com/GameTechDev/PresentMon) as a separate, elevated console
process and reads the CSV it writes. Everything shown comes from that CSV; nothing is estimated.

## How it works

```
 user clicks "Start capture" for game.exe
   │
   ▼
 MonitorService.StartCapture("game.exe")          Idle → Locating
   ├─ PresentMonLocator.Locate()                   PresentMon.exe found + verified? (no execution)
   ├─ ProcessSnapshot.IsRunning("game")            target running? (else ProcessNotFound, no UAC)
   └─ PresentMonSession.Start(target)              Locating → Starting
        Process.Start(UseShellExecute, Verb="runas")  ← the ONLY UAC prompt in normal use
        PresentMon.exe --process_name game.exe --output_file <temp>.csv --session_name VisorPM ...
        tail <temp>.csv every 250 ms (FileShare.ReadWrite | Delete), parse rows by header name
   first FrameRecord                              Starting → Capturing
   every 1 s: FrameStats.Snapshot() → SummaryUpdated + IMetricSink.OnSample
 user clicks "Stop"                                Capturing → Stopping
   PresentMon.exe --terminate_existing_session --session_name VisorPM   ← second UAC prompt
   capturing instance sees its ETW session end and exits → Exited       Stopping → Idle
```

Files (all under `Services\Monitor\`, namespace `Visor.Services`):

| Type | Role |
|---|---|
| `PresentMonLocator` | finds `PresentMon.exe`, verifies it (Authenticode "Intel" via `WinVerifyTrust`, or pinned SHA-256), reads the file version. Never runs it. |
| `PresentMonArgs` | pure command-line builder for the capture and the stop helper. |
| `PresentMonSession` | launches elevated, tails the CSV, raises `Frame` / `Error` / `Exited`. |
| `PresentMonCsvParser` | pure CSV parser (2.x names, 1.x fallbacks, NA → null, header mapping by name). |
| `FrameRecord` | one frame: time, frame time, GPU/CPU busy, display latency, click-to-photon, dropped. |
| `FrameStats` / `FrameStatsSummary` | rolling window (N seconds) → avg FPS, 1 % / 0.1 % low, p99, stutters, bound heuristic. |
| `IMetricSink` / `ExperimentMetricSink` / `MonitorMetricNames` | per-second export of named `Optimize.MetricSample`s (`fps`, `1%low`, `0.1%low`, `frametimeMs`, `frametimeP99Ms`, `stutters`, `clickToPhotonMs` when measured) into `OptimizationExperiment`. |
| `MonitorService` | facade + state machine; the only thing the UI talks to. |

### Why elevation
PresentMon reads the DXGI / DWM / D3D ETW providers. Starting a real-time ETW trace session
needs the *Performance Log Users* privilege or administrator rights; on a normal account that
means a UAC prompt. Visor itself stays `asInvoker` — only `PresentMon.exe` is elevated, only when
the user starts a capture, and never automatically (no auto-start with a game profile, no start
on app launch).

### Why a CSV file instead of stdout
An elevated child's stdout cannot be redirected by a non-elevated parent (`UseShellExecute=true`
is required for `runas`, and that mode has no stream redirection). PresentMon's
`--output_file` is read with `FileShare.ReadWrite | FileShare.Delete`, polling every 250 ms; the
file lives under `%TEMP%\Visor\presentmon\capture-<utc>-<pid>.csv`.

### Stopping, and the second UAC prompt (trade-off)
PresentMon has no IPC, and a non-elevated process cannot terminate an elevated one. The only
clean way to end a named ETW session from outside is to run PresentMon again, elevated, with
`--terminate_existing_session --session_name VisorPM`: it stops the trace session and quits,
and the capturing instance exits when its session disappears. That costs a second UAC prompt.
To make sure a capture can never be orphaned when that prompt is declined, every capture also
carries:

* `--terminate_on_proc_exit` — PresentMon exits when the game exits;
* `--timed <SafetyCapSeconds> --terminate_after_timed` — hard cap (default 3600 s).

Closing Visor does **not** show a prompt: `MonitorService.Dispose` only stops listening; the
elevated capture ends by itself through the two rules above.

### What is measured
From PresentMon 2.x columns (per frame): `FrameTime` (ms between presents), `GPUBusy`,
`CPUBusy`, `DisplayLatency`, `ClickToPhotonLatency` (only when PresentMon could attribute an
input to a frame), `DisplayedTime = NA` → dropped frame. Derived (over the window, default 10 s):

* **avg FPS** = 1000 / mean frame time;
* **1 % low** = 1000 / p99 frame time; **0.1 % low** = 1000 / p99.9 frame time (nearest-rank);
* **p99 / max frame time**, **stutters** = frames slower than 2 × median;
* **bound heuristic** (labelled "likely", with the evidence string): GPUBusy ≥ 85 % of the frame
  time → GPU; GPUBusy ≤ 60 % and CPUBusy ≥ GPUBusy → CPU; both idle → capped / V-Sync / wait;
  fewer than 30 frames or no `GPUBusy` column → unknown;
* **input latency** = mean ClickToPhoton over the frames that carry it (count shown).

### What is NOT measured / not done
* No in-game overlay (Visor's own OSD window only, drawn by the UI layer).
* No hardware (photodiode) latency — ClickToPhoton is PresentMon's software estimate.
* No per-frame data for games PresentMon cannot see (some anti-cheat titles block ETW
  consumers; the capture then produces no rows → `NoOutput`).
* No metrics are synthesised: if a column is missing the value is null and the UI shows "—".
* Processes other than the chosen exe are never captured (`--process_name` filter).

## State machine (`MonitorService`)

```
Idle ──StartCapture──▶ Locating ──▶ Starting(UAC) ──first frame──▶ Capturing ──StopCapture──▶ Stopping
  ▲                       │             │                              │                          │
  │                       ▼             ▼                              ▼                          ▼
  └────ClearError──── Error ◀───────────┴──────────────────────────────┴───────── Exited ────────┘ (→ Idle when clean)
```

`MonitorErrorCode`: `BinaryMissing`, `Unverified`, `UacDeclined` (Win32 1223), `ProcessNotFound`,
`EtwFailed` (PresentMon exited non-zero before any data), `StartFailed`, `NoOutput`,
`OutputUnreadable`, `StopFailed` (stop helper declined → back to Capturing, capture continues),
`Busy`, `InvalidTarget`. One capture at a time; `Locate()` can be called any time for the
settings page without starting anything.

## PresentMon 2.x CLI flags used — verify on real hardware

These come from the PresentMon 2.x README as remembered; they have **not** been run here (no
binary in the tree). Check `PresentMon.exe --help` of the pinned version before the first release:

| Flag | Assumption |
|---|---|
| `--process_name <exe>` | filter by exe file name, case-insensitive, with `.exe` |
| `--process_id <pid>` | alternative filter |
| `--output_file <path>` | single CSV for the filtered process (no `--multi_csv`) |
| `--session_name <name>` | names the ETW session (default "PresentMon") |
| `--stop_existing_session` | a stale session with our name is stopped so the new one can start |
| `--terminate_existing_session` | stop the named session and exit — used by the stop helper |
| `--terminate_on_proc_exit` | exit when the target process exits |
| `--timed <s>` + `--terminate_after_timed` | recording length cap + exit when it elapses |
| `--no_console_stats` | no console UI (we run hidden anyway) |
| `--output_stdout` | **not used** (see "Why a CSV file") |

Open questions for the hardware check: whether 2.x needs `--track_input` for
`ClickToPhotonLatency` to be non-NA (1.x did); whether `--timed` without `--hotkey` starts the
timer at launch; exit code of a session that was ended by `--terminate_existing_session`
(treated as clean when data was produced); whether `--no_console_stats` is still accepted (1.x
spelled it `-no_top`).

Column names handled by the parser: 2.x `CPUStartTime, FrameTime, CPUBusy, GPUBusy / GPUTime,
DisplayLatency, DisplayedTime, ClickToPhotonLatency / AllInputToPhotonLatency, Application,
ProcessID`; 1.x `TimeInSeconds, msBetweenPresents, msGPUActive, msUntilDisplayed, Dropped`.
Unknown columns are ignored; missing optional columns yield nulls; rows are mapped by name so
`--v1_metrics` output and future column reorders keep working.

## Verification of the binary
1. **Authenticode**: `WinVerifyTrust(WINTRUST_ACTION_GENERIC_VERIFY_V2)` with no UI, no network
   revocation (cache-only), and the signer's simple name must contain "Intel".
2. **Pinned SHA-256**: `PresentMonLocator.PinnedHashes` (version → hash). The checked-in entry is
   a placeholder of zeros and can never match; fill it with the hash of the release that
   `build-release.ps1` pins (`$PresentMonSha256`) once it has been computed on a trusted machine.
3. `PresentMonLocator.AllowUnverified` (static, default `false`, not persisted) is a dev-only
   switch for running an unsigned local build of PresentMon.

A file that fails both checks is reported as `Unverified` with the reason and is never launched.

## Dropping the binary in manually
See `tools\presentmon\README.md`. Search order: `<app>\tools\presentmon\PresentMon.exe`,
`%LocalAppData%\Visor\tools\presentmon\PresentMon.exe`, then the path from settings
(`MonitorService.UserConfiguredPath`, file or folder).

## Release bundling
`build-release.ps1 -FetchPresentMon` downloads the pinned asset from the official GitHub
releases URL, verifies SHA-256 (the constant is a `TODO` placeholder that makes the fetch fail
until filled), checks the MZ header, and stages `tools\presentmon\PresentMon.exe` +
`tools\presentmon\README.md` (MIT notice) into the portable zip and the installer payload.
**The installer allowlists must be extended** before that ships: `Installer\InstallerCore.cs`
`IsAllowedPayloadPath`, `Installer\Safety.cs` `IsKnownAppFile`, and `Installer\Legacy.cs`
`KnownFiles` / `KnownDirs` need `tools\presentmon\PresentMon.exe` and `tools\presentmon\README.md`
(the script prints this warning). Default builds do not bundle PresentMon; the monitor then
reports `BinaryMissing` with the folders it searched.

## License
PresentMon is Copyright (C) Intel Corporation, MIT License. The notice ships beside the binary
(`tools\presentmon\README.md`). Visor does not modify PresentMon; it only launches it.
