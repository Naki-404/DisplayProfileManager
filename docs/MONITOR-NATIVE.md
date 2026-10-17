# FPS monitor — built-in frame capture

Visor measures FPS / frame times itself (since 2.2). There is no external tool: nothing to
download, verify, locate or bundle. The capture is raw ETW (Event Tracing for Windows) through
`advapi32` P/Invoke — no NuGet package (TraceEvent was not needed: a few hundred lines of interop
replace a multi-megabyte dependency and keep the AV-heuristics surface small).

Visor does not hook, inject into or overlay games. It only listens to events Windows already logs
when a game presents a frame.

## How it works

```
 user clicks "Start capture" for game.exe
   │
   ▼
 MonitorService.StartCapture("game.exe")                       Idle → Starting
   ├─ IsCaptureSupported?  (Visor.exe, not dotnet/testhost)    else ElevationUnavailable, no UAC
   ├─ ProcessSnapshot.IsRunning("game")                        else ProcessNotFound, no UAC
   └─ NativeFrameCaptureSession.Start(target)
        ├─ resolve PIDs of game.exe (kernel PID list, no handles)
        ├─ create pipe \\.\pipe\VisorFrames-<32 hex>  (1 instance, DACL: user + Administrators, network denied)
        ├─ write %LocalAppData%\Visor\elevation\<id>.cap.json   (folder ACL: user, Administrators, SYSTEM)
        └─ Process.Start("Visor.exe", "--frame-capture <id>.cap.json", Verb=runas, hidden)   ← the ONE UAC prompt
                 │
                 ▼   elevated Visor.exe --frame-capture (FrameCaptureHost, before any GUI/log/mutex/config)
            validate request path + content → connect to the pipe (anonymous impersonation level)
            → pipe server PID must equal the request's parent PID → refuse unless elevated
            → re-resolve targets → EtwSession.Start → "Started"
            ETW callback → PresentTracker → 25-byte frame records → pipe
   first frame                                                 Starting → Capturing
   every 1 s: FrameStats.Snapshot() + BoundEstimator → SummaryUpdated + IMetricSink.OnSample
 user clicks "Stop": session writes one stop byte              Capturing → Stopping
   host: ControlTrace(STOP) → "Stopped" → closes pipe → exits    Stopping → Idle
```

Files (`Services\Monitor\`, namespaces `Visor.Services` / `Visor.Services.FrameCapture`):

| Type | Role |
|---|---|
| `Native\EtwNative` | blittable mirrors of `EVENT_TRACE_PROPERTIES`, `EVENT_TRACE_LOGFILEW`, `EVENT_RECORD`, `ENABLE_TRACE_PARAMETERS`, … + `StartTraceW`, `ControlTraceW`, `EnableTraceEx2`, `OpenTraceW`, `ProcessTrace`, `CloseTrace`. x64 offsets are written next to every field and asserted by `FrameCaptureEtwLayoutTests`. |
| `Native\EtwSession` | the real-time session `VisorFrameCapture` (only ever created inside the elevated host). |
| `Native\PresentEvents` | provider GUIDs / event IDs / keywords, `PresentEvent`, the `IPresentEventSource` seam. |
| `Native\PresentTracker` | pure: present events → `FrameRecord`s (channel selection, main swap chain, gaps). |
| `Native\FrameWire` | pure: host → Visor byte protocol (encoder + incremental decoder). |
| `Native\FrameCaptureRequest` | request model, strict JSON, path rules, validation. |
| `Native\FrameCaptureHost` | body of `Visor.exe --frame-capture`. |
| `Native\NativeFrameCaptureSession` | unelevated side: pipe server, request file, runas launch, reader. |
| `FrameCaptureSession` | `IFrameCaptureSession`, `FrameCaptureTarget`, `FrameCaptureErrorCode`. |
| `BoundEstimator` | pure "likely GPU / CPU / capped" heuristic + `IBoundSignals` (NvAPI GPU load, refresh). |
| `MonitorService` | facade + state machine; the only thing the UI talks to. |
| `FrameStats`, `FrameRecord`, `MetricSink` | unchanged contracts (rolling window, 1 % / 0.1 % lows, stutters, metric export). |

## ETW session

* `StartTraceW("VisorFrameCapture")`: `WNODE_FLAG_TRACED_GUID`, `ClientContext = 1` (QPC clock),
  `EVENT_TRACE_REAL_TIME_MODE`, 64 KB buffers, `FlushTimer = 1 s`, logger name buffer right after
  the 120-byte struct. A stale session with the same name (crashed host) is stopped first;
  `ERROR_ALREADY_EXISTS` → stop + retry once.
* `EnableTraceEx2` per provider, level 4, with `ENABLE_TRACE_PARAMETERS` v2 filters:
  `EVENT_FILTER_TYPE_EVENT_ID` (only the IDs below) and, for the user-mode providers DXGI / D3D9,
  `EVENT_FILTER_TYPE_PID` when there are 1–8 targets. If the OS refuses a filter the provider is
  re-enabled with fewer filters (ids only → unfiltered). `EnableSummary` records what was applied.
  The callback filters by `EventHeader.ProcessId` anyway.
* `OpenTraceW`: `PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP`;
  the `EventRecordCallback` delegate is a field (kept alive for the session's lifetime).
  `ProcessTrace` runs on a dedicated background thread.
* Stop: `ControlTraceW(STOP)` → `CloseTrace` → join (3 s). Called from `finally` on every host path.
* Timing: `EVENT_HEADER.TimeStamp` (raw QPC) — no payload decoding is needed for frame times.
  Frequency = `LogfileHeader.PerfFreq` (falls back to `Stopwatch.Frequency`, the same QPC).

## Providers and events

Verified against the providers' manifests as published in Intel's open-source frame-capture tool
(generated headers `Microsoft_Windows_DXGI.h`, `Microsoft_Windows_D3D9.h`, `Microsoft_Windows_DxgKrnl.h`,
MIT) and its trace consumer. Only IDs / GUIDs / field order were taken — no code.

| Provider | GUID | Event (ID) | Keyword used | Confidence |
|---|---|---|---|---|
| Microsoft-Windows-DXGI | `CA11C036-0102-4A2D-A6AD-F03CFED5D3C9` | Present_Start (42), PresentMultiplaneOverlay_Start (55) | `0x2` (Events) | high — IDs, GUID and payload field order (`pIDXGISwapChain, Flags, SyncInterval`) match the reference consumer |
| Microsoft-Windows-D3D9 | `783ACA0A-790E-4D7F-8451-AA850511C6B9` | Present_Start (1) | `0x2` (Events) | high (payload `pSwapchain, Flags`) |
| Microsoft-Windows-DxgKrnl | `802EC45A-1E99-4B83-9920-87C98277BA9D` | Present_Info (184), PresentHistoryDetailed_Start (215), PresentHistory_Start (171), Flip_Info (168), Blit_Info (166), FlipMultiPlaneOverlay_Info (252) | `0x8000000` (Present) | IDs high (from the header); *which* of them fires once per frame for a given non-DXGI title is medium — see the fallback plan |

Swap-chain separation: the first DXGI / D3D9 payload field is the swap-chain pointer; its width
follows `EVENT_HEADER_FLAG_32_BIT_HEADER` (0x20) / `_64_BIT_HEADER` (0x40). DxgKrnl events are
tracked per process (no payload decoding).

### Frame tracking (`PresentTracker`)

* Each event goes on a *channel*: DXGI (rank 0), D3D9 (1), then DxgKrnl Present_Info (2),
  PresentHistoryDetailed (3), PresentHistory (4), Flip (5), Blit (6), FlipMPO (7).
* Per process, exactly one channel counts: the lowest rank seen ≥ 3 times and active within 1 s.
  A DXGI game is therefore never double-counted by the kernel events of the same present.
* Within the channel each swap chain is a stream; across all targets the stream with the highest
  recent present rate (decaying score, 1 s half-life, 1.25× hysteresis) is the game. Launcher /
  overlay swap chains with a lower rate are ignored.
* Frame time = present-to-present delta of that stream. Deltas ≤ 0 are dropped, > 5 s are a gap
  (alt-tab / loading: no frame, the stream restarts). After a stream switch an interval that
  overlaps the last emitted frame is skipped (a switch costs at most one frame, never adds one).

## What is measured — and what is not

Measured: FPS, frame time (present-to-present, i.e. the application-side "ms between presents"),
1 % / 0.1 % lows, p99, stutters, frame-time variation.

**Not measured** (the fields exist and stay null; the UI shows "—"): displayed vs dropped frames,
display latency, click-to-photon, GPU busy, CPU busy. They need full present
lifecycle tracking (DWM, flip queue, vsync DPC, input events) which is out of scope here.
Frame times are *present* intervals, not *display* intervals: with V-Sync or a compositor the
on-screen cadence can differ, and frame generation (DLSS-FG / FSR-FG / AFMF) may or may not show
up as presents depending on how the vendor implements it (see the checks below).

### Limiter estimate (`BoundEstimator`, labels are always "likely")

1. GPU load ≥ 95 % (NvAPI via `SensorService`, averaged over the stats window, busiest GPU) → likely GPU-bound.
2. FPS within ±3 % of the display refresh and frame-time variation ≤ 10 % → likely capped (V-Sync / limiter).
3. No GPU load reading (AMD / Intel without sensors) → unknown, evidence still shows FPS and refresh.
4. GPU load ≤ 75 % and FPS < 90 % of refresh → likely capped if very steady (variation ≤ 4 %: an FPS
   limiter), otherwise likely CPU-bound.
5. Anything else → "neither clearly saturated" (capped / balanced).

The evidence string carries the numbers, e.g. `GPU load 60 % at 72 FPS, well below 144 Hz refresh,
frame-time variation 12 %`. Refresh = primary display's current mode at capture start.

## Why one UAC prompt, and why only one

A real-time ETW session needs administrator rights (or *Performance Log Users* membership).
Visor itself stays `asInvoker`; only the short-lived `Visor.exe --frame-capture` helper is
elevated, only when the user starts a capture. Stopping needs **no** second prompt: the helper
listens for a stop byte on the pipe Visor created (the external tool used before 2.2 had no IPC
and needed a second elevated launch just to stop).

## Security of the elevated helper

* **Does one thing.** Reads its request file, connects to one pipe, runs one ETW session, writes
  frame records to the pipe. No registry, no file writes, no process launches, no logging.
* **Request validation** (`FrameCaptureRequestJson`): absolute local path (no UNC, no `..`), file
  `<32-hex id>.cap.json` directly in a `…\Visor\elevation` folder that is not a junction (nor its
  parent), ≤ 4 KB, strict JSON (unknown members rejected), id in the file = file stem, pipe name
  `VisorFrames-<32 lowercase hex>`, parent PID > 4, ≤ 32 target PIDs (none ≤ 4, no duplicates,
  not the parent), optional plain exe name (no paths, not a protected system process), safety cap
  1–3600 s. Anything else → exit 2 without connecting anywhere.
* **Pipe**: created by unelevated Visor, one instance, `FirstPipeInstance` (squatting fails),
  DACL = current user (full) + Administrators (read/write), NETWORK denied. The helper connects with
  `TokenImpersonationLevel.Anonymous` (Visor's side can never impersonate the elevated token) and
  checks `GetNamedPipeServerProcessId == parent PID` before doing anything else.
* **Elevation check** after the pipe is up, so a refusal can be reported; nothing ETW-related runs
  before it.
* **Bounded lifetime**: stops on the stop byte, a broken / closed pipe, the parent Visor exiting
  (`WaitForExitAsync` on the parent PID), every target exiting (1 s poll), the ETW session ending
  from outside, or the safety cap — whichever comes first. `ControlTrace(STOP)` is in `finally`.
  Disposing the session (Visor closing) simply closes the pipe.
* Frames beyond 8192 queued records are dropped rather than blocking the ETW callback.

## Wire protocol (host → Visor)

`'V' 'F' 0x01` hello, then messages = type byte + fixed little-endian payload:

* `0x01` frame (24 B): f64 time s, f64 frame time ms, i32 pid, u8 source (1 DXGI, 2 D3D9, 3 DxgKrnl), 3 reserved.
* `0x02` status (8 B): u8 kind (1 Started[detail = #targets], 2 Error[code = HostError, detail = Win32],
  3 TargetExited, 4 Stopped[code = HostStopReason]), u8 code, u16 reserved, i32 detail.

Visor → host: one byte, `'S'` = stop. The decoder fails closed on an unknown version / type /
status kind / non-finite values and keeps partial messages across reads.

## Error mapping

| Host / session | `MonitorErrorCode` |
|---|---|
| launcher throws Win32 1223 | `UacDeclined` |
| launch failure, helper exit before connecting, not elevated, bad request, pipe owner mismatch, internal | `StartFailed` |
| `EtwStartFailed` (Win32 5 access denied, 1450 session limit, …) | `EtwFailed` |
| `ProcessNotFound` (no target alive when the helper looked) | `ProcessNotFound` |
| no frame for 5 s (advisory; final only if the capture ends without frames) | `NoOutput` |
| malformed stream | `OutputUnreadable` |
| not running as Visor.exe (dotnet host / test runner) | `ElevationUnavailable` (new) |

`BinaryMissing`, `Unverified`, `StopFailed` and `MonitorState.Locating` are no longer produced;
they stay in the enums only because the current UI references them.

## Must be verified on real hardware (not possible in unit tests)

Run each with the game in focus for ≥ 30 s, compare against the game's own FPS counter or a known
cap, and check `%AppData%\Visor\Visor.log` for `Frame capture:` lines.

1. **DX11 game**, borderless windowed and exclusive fullscreen: FPS matches an in-game cap
   (e.g. 60 / 120), frame-time graph flat; one UAC prompt; Stop needs no prompt; helper process
   (`Visor.exe --frame-capture`) disappears within ~1 s of Stop.
2. **DX12 game** (flip model), with and without V-Sync: pinned at refresh → "likely capped";
   GPU-heavy settings → "likely GPU-bound" on NVIDIA.
3. **Vulkan game** (e.g. Doom Eternal / RDR2 Vulkan): frames arrive. Note which channel was used
   (add a debug log of `PresentTracker.MainStream` if needed): DXGI on drivers that layer Vulkan on
   DXGI swap chains, otherwise a DxgKrnl event. FPS must match the in-game counter — if it is ~2×,
   the chosen DxgKrnl event fires twice per frame (see the fallback plan).
4. **OpenGL game** (e.g. Minecraft Java): same checks as Vulkan.
5. **Borderless vs exclusive fullscreen** for one title of each API: same FPS in both.
6. **Frame-generation titles** (DLSS 3 FG / FSR 3 FG / AFMF): record whether the reported FPS is
   the rendered or the generated rate and document it in the UI copy (AFMF is driver-side and
   likely invisible to present events).
7. **Session hygiene**: kill Visor (Task Manager) during a capture → the helper exits within ~1 s
   and `logman query -ets` no longer lists `VisorFrameCapture`. Start a capture with another
   ETW capture tool (e.g. CapFrameX) running → both work (different session names).
8. **Game exit** during capture → Visor returns to Idle by itself (TargetExited).
9. **Anti-cheat titles** (EAC / BattlEye / Vanguard): capture works (no handles are opened to the
   game except a liveness query that tolerates denial).
10. **UAC declined** → "permission declined" card; **standard (non-admin) account** with
    credentials prompt works the same.
11. `EnableSummary` in a debug session: confirm the event-ID and PID filters are accepted
    (`dxgi=ids+pids d3d9=ids+pids dxgkrnl=ids`). If `unfiltered` shows up, overhead is higher but
    results are unaffected.

## Fallback plan if DxgKrnl IDs / semantics differ

The DxgKrnl IDs come from the current published header, but kernel manifests change across Windows
builds. If a non-DXGI title shows no frames or a doubled rate:

1. Dump the provider manifest on that build: `wevtutil gp Microsoft-Windows-DxgKrnl /ge /gm:true`
   and compare IDs / versions with `PresentProviders.DxgKrnlEventIds`.
2. Adjust the IDs and/or `PresentTracker.ChannelRank` order (one table each; tests pin the order).
3. If no kernel event is reliably once-per-frame, restrict the kernel channel to Present_Info +
   PresentHistoryDetailed_Start and decode `Token` (first payload field of PresentHistory*) to
   deduplicate per present — the tracker already supports per-stream keys.
4. Worst case, disable the DxgKrnl provider (remove it from `EnableProviders`): DXGI / D3D9 titles
   (the vast majority, including Vulkan/OpenGL layered on DXGI) keep working; others show
   `NoOutput` with the "API the capture does not see" detail.

## UI

* Monitor page: Start shows the one-time UAC explanation (`monitor.uac.start`) once per app session,
  then calls `MonitorService.StartCapture`; Stop calls `StopCapture` directly (no dialog, no prompt).
  When `IsCaptureSupported` is false (dotnet host / test runner) Start is disabled and its tooltip
  says why (`monitor.err.elevationUnavailable`).
* The click-to-photon tile stays hidden (the engine does not measure it); the frames line and the
  chart legend no longer mention dropped frames.
* Settings → FPS monitor: only the FPS overlay toggle. The 2.1-era config value holding the external
  tool's path is ignored on load (unknown JSON members are skipped).
