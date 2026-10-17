# PC tweaks: startup apps, Ultimate plan, GPU scheduling, cache cleanup, visual effects

Backends for the Optimize → PC panel. Code: `Services/System/Optimize/Pc/*`
(namespace `Visor.Services.Optimize.Pc`). Rules that apply to all of them:

- Nothing is applied without a click. Listing, scanning and status reads never change anything.
- Every change except cache cleanup is a SafeAction: the exact previous state is journaled
  (`safeactions.json`) and undo writes it back byte for byte, or deletes the value if it was absent.
- Changes that need administrator rights go only through the elevated helper
  (`IElevationBroker`, see `NETWORK-TWEAKS.md`): one UAC prompt per apply or undo, allowlisted ops.
  These targets report `RequiresElevation = true` and must be called with
  `SafeActionService.ApplyAsync` / `UndoAsync`. The synchronous path refuses them with `needsElevation`.
- No placebo: no "boost" scores, no registry cleaning, no service disabling, no debloat. Startup
  "impact" is the file size, because that is all Visor can actually measure.
- Everything works inside Visor (in-app pass, 2026-10-17): no button opens Event Viewer, Performance
  Options or a Windows Settings page any more. See §4 (Delivery Optimization), §7 (visual effects) and §8
  (stability events).

Wiring (UI side):

```csharp
var pc = PcTweaks.Create(Config.ConfigDirectory, broker, setPlan: Display.SetPowerPlan);
pc.Register(safeActions);                  // hags.on / hags.off / ultimatePlan.on / visualFx.performance + journaled startup toggles
var apps = pc.Startup.List();              // read-only
pc.Startup.Register(safeActions, apps);    // per-entry startup.disable|… / startup.enable|… targets
await safeActions.ApplyAsync(apps[i].DisableActionId);
```

## 1. Startup apps — `StartupAppsService`

| Source | Listed from | On/off switch | Toggle |
|---|---|---|---|
| `UserRun` | HKCU `…\CurrentVersion\Run` | HKCU `…\Explorer\StartupApproved\Run` | yes, no admin |
| `UserStartupFolder` | `shell:startup` (`.lnk` resolved without network access) | HKCU `StartupApproved\StartupFolder` | yes, no admin |
| `MachineRun` | HKLM `…\CurrentVersion\Run` | HKLM `StartupApproved\Run` | yes, elevated helper (`RegSetBinary` / `RegDeleteValue`) |
| `MachineRun32` | HKLM `WOW6432Node\…\Run` | HKLM `StartupApproved\Run32` | yes, elevated helper (same ops as `MachineRun`) |
| `CommonStartupFolder` | `shell:common startup` | HKLM `StartupApproved\StartupFolder` | yes, elevated helper (same ops as `MachineRun`) |

All three HKLM sources use `ElevatedStartupToggleTarget(enable, location, name, …)`; the three
`StartupApproved` keys are the only HKLM startup keys in `ElevationAllowlist`. An entry is read-only
(`StartupToggleBlock.NoBroker`) only when no elevation broker was supplied (tests / dev host).

- **Switch format** (checked against a real Windows 11 registry): 12-byte REG_BINARY.
  `02 00…00` = enabled; `03 00 00 00` + FILETIME of the change = disabled (`07…` also seen = disabled).
  The low bit of the first byte means disabled, and an absent value means enabled. Visor writes exactly
  what Task Manager writes.
- **Never deletes** a Run value or a Startup-folder file. Disable and enable only flip the
  StartupApproved value. Undo restores the exact previous bytes, including the original FILETIME,
  or deletes the value.
- Columns: name, command, publisher (`FileVersionInfo.CompanyName`), file size. These come only from
  local `X:\` paths, never from UNC paths.
- Action ids: `startup.disable|<Location>|<name>`, `startup.enable|<Location>|<name>`. Both share the
  journal slot `startup|<Location>|<name>`. Loc prefix: `optimize.startup` (the entry name is a format argument).
- After a restart, `RegisterJournaled` rebuilds the targets for journaled entries, so undo still works
  even when the entry has since gone from the Run key.

**Honest impact:** sign-in gets faster and less runs in the background. In-game FPS does not change.
Disabling a driver tray tool or security software can remove features. Only disable what you recognise.

## 2. Ultimate Performance plan — `UltimatePlanService` + `UltimatePlanTarget`

- **Detect** (no admin): `PowerEnumerate` plus the raw HKLM `FriendlyName`. Duplicates keep the
  language-neutral `@…powrprof.dll,-19,…`, so detection works on Russian or any other Windows. The
  fallbacks are the GUID Visor created (stored in `pc-tweaks.json`, so it survives a rename) and a list
  of localized names. The hidden template `e9a42b02-…` is not a usable scheme on standard installs.
- **Create** (separate click, admin): one `PowerDuplicateScheme` op, which the helper allowlists for the
  template GUID only. Visor checks that the new GUID is really listed before it stores it. We cannot
  verify that `powercfg /duplicatescheme` works without admin on every build, so creating always goes
  through the helper.
- **Activate** (`ultimatePlan.on`, no admin): `DisplayEngine.SetPowerPlan(guid)`. State = the active
  scheme GUID. Undo switches back to the exact previous GUID. It is blocked while a game session owns
  the power plan.
- **Remove** (separate click, admin): `UltimatePlanService.RemoveCreatedAsync` deletes only the GUID
  stored in `pc-tweaks.json` as created by Visor — never another Ultimate scheme, never a built-in one.
  It refuses while that scheme is the active plan (switch away first, e.g. Undo) and during a game
  session. One `PowerDeleteScheme` op (Target "", Value = the GUID); the helper accepts only a
  well-formed "D"-form GUID that is not Balanced / High performance / Power saver / the Ultimate
  template, and runs `powercfg /delete <guid>`. Visor checks that the scheme is gone before it forgets
  the GUID. Visor never deletes it automatically.
- There is no recommendation rule for it. Visor already switches plans per game, and a permanent
  "more power" suggestion would be nagging.

**Honest impact:** this helps desktops, mainly outside games, because the per-game plan already covers
games. It means more heat, more fan noise and more power draw all the time, and it is pointless on
battery. There is no FPS promise.

## 3. Hardware-accelerated GPU scheduling — `HagsTarget`

- Value: HKLM `SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` (DWORD 2 = on, 1 = off,
  absent = driver default). It is written only through the helper (`RegSetDword` / `RegDeleteValue`).
- **Running state** (no admin): gdi32 `D3DKMTQueryAdapterInfo`. `KMTQAITYPE_DRIVERVERSION` (13) gives
  the WDDM version. `KMTQAITYPE_WDDM_2_7_CAPS` (70) gives `HwSchSupported` / `HwSchEnabled`. On the test
  machine, NVIDIA on WDDM 3.2 reported `0xB` (supported + enabled) with `HwSchMode` absent, so an absent
  value does **not** mean "off".
- `HagsStatus.PendingRestart` = the registry asks for a state the kernel is not running yet.
- `hags.on` is a no-op when the value is absent but the kernel already runs HAGS. `hags.on` and
  `hags.off` share the journal slot `hagsMode`. Undo restores the exact previous DWORD, or deletes it if
  it was absent. That also takes a UAC prompt and a restart.
- UI: Optimize → System and Optimize → Graphics render the same row (`BuildHagsRow`, same target and journal
  slot). The former `ms-settings:display-advancedgraphics` link in Graphics is gone.

**Honest impact:** the effect depends on the GPU and driver, and there is often no measurable change.
It needs a restart. Some capture tools and older titles behave worse with it on.

## 4. Cache cleanup — `CleanupService` (not journaled)

| Category | Roots | Filter |
|---|---|---|
| `UserTemp` | `%TEMP%` | only items whose newer of creation / last-write time is > 24 h old |
| `DirectXShaderCache` | `%LocalAppData%\D3DSCache` | all |
| `NvidiaShaderCache` | `%LocalAppData%\NVIDIA\DXCache`, `GLCache`, `%ProgramData%\NVIDIA Corporation\NV_Cache` (whatever is accessible) | all |
| `AmdShaderCache` | `%LocalAppData%\AMD\DxCache`, `DxcCache`, `GLCache` | all |
| `DeliveryOptimization` | `%windir%\ServiceProfiles\NetworkService\…\DeliveryOptimization\Cache` | not walked by `CleanupService` (owned by NetworkService). Size and clearing: `DeliveryOptimizationService`, below |

- `Scan()` only measures. `Clean(category)` is one category per explicit click and reports freed bytes,
  deleted files, skipped files (locked / in use, read-only, system, access denied) and removed empty folders.
- Guard: roots must be local `X:\…` paths at least two folders deep. A root cannot be a protected folder
  itself (profile, AppData, Local, ProgramData, Program Files, Desktop, Documents, Downloads), cannot be
  inside `%windir%`, and cannot be a reparse point. Reparse points under a root are never entered and
  never deleted. Every deleted file must be strictly inside its root, and its parent must still be a
  plain folder at delete time. The root folder itself is never removed.
- The 24 h age uses the newer of creation and last-write time. An installer that is extracting
  old-timestamped files right now is therefore left alone.

**Honest impact:** frees disk space and nothing else. Shader caches are rebuilt, so the **first launch
of each game may stutter once** while shaders compile again. This cannot be undone, but there is
nothing to undo: these are caches.

### Delivery Optimization cache — `DeliveryOptimizationService` (one UAC prompt, not journaled)

- **Size** (no admin): WMI `root\Microsoft\Windows\DeliveryOptimization`, `MSFT_DOCurrentStatus.CacheSizeBytes`
  (the number `Get-DeliveryOptimizationPerfSnap` shows; checked readable by a standard user on Windows 11).
  Fallback: a walk of the cache folder, which a standard user cannot read → the row then says "Size needs
  administrator rights to read" instead of guessing. Measured together with the cache scan.
- **Clear**: one elevated-helper op `ElevatedOpKind.DeliveryOptimizationClear` with **no parameters**
  (allowlist: `Target == ""`, `Name` / `Value` null, anything else → `unexpectedParameters`). The helper runs
  `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoLogo -NoProfile -NonInteractive -Command
  "$ErrorActionPreference = 'Stop'; Delete-DeliveryOptimizationCache -Force | Out-Null"` — a fixed string, never
  concatenated with input, 45 s timeout (inside the 60 s broker timeout), process tree killed on timeout
  (`WindowsPowerShellRunner`, accepts only `powershell.exe`). No `-ExecutionPolicy` switch: the Microsoft-signed
  module in System32 loads under the default `Restricted` policy (checked). Pinned files are kept (no
  `-IncludePinnedFiles`).
- **Freed bytes**: the helper measures the WMI size before and after and returns the difference as the op output.
  If it cannot, the UI side compares its own before / after reads; if neither works the toast says "cleared"
  without a number.
- UI (Optimize → Cleanup): size line, the admin note, "Clean (one administrator prompt)" and the note
  "Cannot be undone, but safe: Windows Update downloads anything it needs again from Microsoft". The old
  `ms-settings:storagesense` link is gone. History entry: `cleanup` / `DeliveryOptimization|<bytes>`.

## 5. Recommendations (`RecommendationEngine`)

| Id | When | Severity | Action |
|---|---|---|---|
| `rec.startup.many` | ≥ 10 enabled startup entries | Info | `OpenStartupApps`. Evidence: count + the 8 largest entries with file sizes |
| `rec.cleanup.disk` | the cache drive has < 10 % free **and** ≥ 500 MB was measured as reclaimable | Tip | `OpenCleanup`. Evidence: bytes per category. Bytes are kept out of the body arguments so a re-scan does not bring back a dismissed tip |
| `rec.hags.off` | caps say supported (WDDM ≥ 2.7), kernel off, registry not already on, no restart pending | Info | `SafeActionHagsOn` (`hags.on`, admin + restart) |
| Ultimate plan | never | | |

Context inputs: `StartupApps`, `CleanupScan` + `CleanupDrive` (`CleanupService.PrimaryDrive()`) and
`Hags` (`HagsStatusReader.Read()`). A null input means "not measured", and no rule fires on it.

## 6. Session audio switch fix (`SessionExtrasService` / `AudioEndpoint`)

The owner's log showed `Session extras — Audio device: SetDefaultEndpoint(Console) failed` on activation
and again on the deferred re-apply 3 s later. Restoring to another device with the same COM code
succeeded seconds later, so the interface and vtable were fine. The profile's stored endpoint id
`{0.0.0.00000000}.{13332c2c-…}` **no longer exists**: endpoint ids change when a device is reinstalled
or moved to another port. `ResolveId` accepted any `{…}` string unchecked, and `SetDefaultEndpoint`
failed (E_NOTFOUND) on every apply. The HRESULT was never logged.

The fix:

- `ResolveUsable` checks `IMMDeviceEnumerator.GetDevice(id)` + `IMMDevice.GetState`. If the device is
  missing or inactive, the switch is skipped (the snapshot is not marked changed) and one warning per id
  per run tells the user to re-select the device.
- PolicyConfig: `IPolicyConfig` (Win7–11, `F8679F50-…`, 10 methods before `SetDefaultEndpoint`) first,
  then `IPolicyConfigVista` (`568B9108-…`, 9 methods before it) if the first QI fails. CLSID
  `870AF99C-…` is unchanged (AudioSes.dll, ThreadingModel=Both).
- The HRESULT is logged with its name (`AudioEndpointId.DescribeHResult`). After a failure, the same id
  waits 10 minutes before the next attempt (`AudioSwitchMemo`).
- Restore: a device that no longer exists is logged and treated as done, so it is not retried on every
  start. A real failure keeps the flag so the restore is retried later.

## 7. Visual effects — `VisualEffectsSafeActionTarget` (`visualFx.performance`, HKCU, no admin)

"Best performance, keep it readable" as one journaled action (Optimize → Graphics; replaces the
"Open Performance Options" card).

| Value (HKCU) | Kind | Apply | Live apply |
|---|---|---|---|
| `Control Panel\Desktop\UserPreferencesMask` | REG_BINARY | clears the bits of menu animation, combo-box slide, list-box smooth scrolling, menu fade, selection fade, tooltip animation + fade, cursor shadow, shadow under windows; every other bit and byte unchanged | `SPI_SETMENUANIMATION`, `SPI_SETCOMBOBOXANIMATION`, `SPI_SETLISTBOXSMOOTHSCROLLING`, `SPI_SETMENUFADE`, `SPI_SETSELECTIONFADE`, `SPI_SETTOOLTIPANIMATION`, `SPI_SETTOOLTIPFADE`, `SPI_SETCURSORSHADOW`, `SPI_SETDROPSHADOW` (BOOL in pvParam) |
| `Control Panel\Desktop\WindowMetrics\MinAnimate` | REG_SZ | `"0"` | `SPI_SETANIMATION` with `ANIMATIONINFO` |
| `Software\…\Explorer\Advanced\TaskbarAnimations` | DWORD | 0 | `WM_SETTINGCHANGE` "TraySettings" |
| `Software\Microsoft\Windows\DWM\EnableAeroPeek` | DWORD | 0 | `WM_SETTINGCHANGE` |
| `Software\…\Explorer\VisualEffects\VisualFXSetting` | DWORD | 3 (Custom, so the Windows dialog shows the truth) | — |

- Bit layout: SPI_SET* action `a` ↔ bit `(a − 0x1001) / 2` of the mask (menu animation = byte 0 `0x02`, cursor
  shadow = byte 1 `0x20`, drop shadow = byte 2 `0x04`). An absent mask is treated as the Windows 10 / 11 default
  `9E 3E 07 80 12 00 00 00`; a malformed mask (not binary, or < 4 bytes) is never rewritten.
- **Deliberately kept on**: ClearType / font smoothing, thumbnails, window contents while dragging (users rely
  on it, no measurable cost), the translucent selection rectangle, desktop icon-label shadows (`ListviewShadow`:
  labels become hard to read on light wallpapers) and the UI-effects master switch (`SPI_SETUIEFFECTS`: it also
  kills hot-tracking). None of these values is owned by the target, so undo never touches them either.
- SPIs are called **without** `SPIF_UPDATEINIFILE` (`SPIF_SENDCHANGE` only): the registry already holds the exact
  bytes Visor wrote, and the SafeAction service verifies by re-reading them. Undo writes back every exact value
  (deletes the absent ones) and re-runs the same SPIs with the restored bits.
- "Satisfied" ignores `VisualFXSetting` (bookkeeping only), so an already-lean desktop is a NoOp.
- **Honest impact:** desktop and Explorer feel snappier; **no effect on in-game FPS**. Taskbar animations and Peek
  may switch fully only after the next sign-in.

## 8. Stability events — in-app list (My PC → Stability)

`CrashEventReader.ReadRecent(50)` reads the same System-log records the stability summary counts
(Kernel-Power 41, WHEA 17/18/19, Display 4101, nvlddmkm / amdkmdag / igfx errors), newest first, last 30 days,
with `EventRecord.FormatDescription()` off the UI thread. Messages are sanitized: the user's profile path and any
`X:\Users\<name>` become `%USERPROFILE%`, runs of blank lines collapse, 2000 characters max. The card lists a kind
badge, time, provider, event id and the first line; "Full text" expands; the strip filters by kind (only kinds
that occur); "Copy details" puts an invariant-English plain-text report of the shown events on the clipboard.
Replaces the former "Open Event Viewer" button.

## 9. Loc keys the UI needs

| Key | EN | RU |
|---|---|---|
| `optimize.startup.title` | Startup apps | Автозагрузка |
| `optimize.startup.why` | Turns a program's start at sign-in off or on with the same switch Task Manager uses. The program and its Run entry stay untouched. | Включает или отключает запуск программы при входе тем же переключателем, что и Диспетчер задач. Сама программа и её запись Run не меняются. |
| `optimize.startup.risk` | A disabled app no longer starts with Windows (tray tools, updaters, sync clients). Start it manually when needed. | Отключённая программа больше не стартует с Windows (значки в трее, обновляторы, синхронизация). Запускайте её вручную при необходимости. |
| `optimize.startup.undo` | Undo writes back the exact previous switch value, or removes it if there was none. | Отмена возвращает точное прежнее значение переключателя или удаляет его, если его не было. |
| `optimize.startup.size` | File size: {0} | Размер файла: {0} |
| `optimize.startup.sizeUnknown` | File not found | Файл не найден |
| `optimize.startup.publisherUnknown` | Unknown publisher | Издатель неизвестен |
| `optimize.startup.impactNote` | Visor shows file size only: Windows has no reliable startup-impact number. | Visor показывает только размер файла: надёжной оценки влияния на запуск у Windows нет. |
| `optimize.startup.admin` | Installed for all users: needs administrator approval (UAC). | Установлено для всех пользователей: нужно подтверждение администратора (UAC). |
| `optimize.startup.readOnly` | Needs Visor's administrator helper, which isn't available here. | Нужен помощник администратора Visor, а здесь он недоступен. |
| `optimize.startup.enable` | Enable | Включить |
| `optimize.startup.disable` | Disable | Отключить |
| `optimize.ultimatePlan.title` | Ultimate Performance power plan | Схема «Максимальная производительность» |
| `optimize.ultimatePlan.why` | Windows' hidden plan for desktops: removes small CPU and device power-saving delays. Visor already switches plans while a game profile runs, so this mainly matters outside games. | Скрытая схема Windows для ПК: убирает мелкие задержки энергосбережения процессора и устройств. Visor и так переключает схему на время игры, поэтому это важно в основном вне игр. |
| `optimize.ultimatePlan.risk` | More heat, fan noise and power draw all the time. Not for laptops on battery. No FPS promise. | Больше нагрев, шум и энергопотребление всё время. Не для ноутбука от батареи. Прирост FPS не обещается. |
| `optimize.ultimatePlan.undo` | Undo switches back to your previous plan. The plan Visor created stays in the list until you click “Remove plan” here. | Отмена возвращает прежнюю схему. Созданная Visor схема остаётся в списке, пока вы не нажмёте здесь «Удалить схему». |
| `optimize.ultimatePlan.missing` | Not installed. Create it first (one UAC prompt). | Схема не установлена. Сначала создайте её (один запрос UAC). |
| `optimize.ultimatePlan.create` | Create plan | Создать схему |
| `optimize.ultimatePlan.created` | Plan created | Схема создана |
| `optimize.ultimatePlan.remove` | Remove plan | Удалить схему |
| `optimize.ultimatePlan.remove.active` | This plan is active — switch to another plan first (Undo), then remove it. | Эта схема сейчас активна — сначала переключитесь на другую («Отменить»), затем удалите. |
| `optimize.ultimatePlan.removed` | Plan removed | Схема удалена |
| `optimize.hagsMode.title` | Hardware-accelerated GPU scheduling | Планирование GPU с аппаратным ускорением |
| `optimize.hagsMode.why` | Lets the GPU schedule its own work (WDDM 2.7+). Latency may drop slightly on some GPU and driver combinations. Often there is no measurable change. | Видеокарта сама планирует свою работу (WDDM 2.7+). На некоторых связках GPU и драйвера задержка может немного снизиться. Часто разницы не видно. |
| `optimize.hagsMode.risk` | Needs administrator approval and a restart. Some capture tools and older games behave worse with it on. | Нужны права администратора и перезагрузка. Некоторые программы записи и старые игры работают с ним хуже. |
| `optimize.hagsMode.undo` | Undo writes back the exact previous value (UAC again) and applies after a restart. | Отмена возвращает точное прежнее значение (снова UAC) и действует после перезагрузки. |
| `optimize.hagsMode.pendingRestart` | Restart pending: the change applies after the next restart. | Ожидает перезагрузки: изменение вступит в силу после перезапуска. |
| `optimize.hagsMode.unsupported` | Your GPU driver does not support it. | Драйвер видеокарты это не поддерживает. |
| `optimize.cleanup.title` | Clean caches | Очистка кэшей |
| `optimize.cleanup.why` | Removes temporary files older than 24 h and GPU shader caches to free disk space. | Удаляет временные файлы старше 24 ч и кэши шейдеров, чтобы освободить место. |
| `optimize.cleanup.risk` | Shader caches are rebuilt: the first launch of each game may stutter once while shaders compile. Files in use are skipped. | Кэши шейдеров пересоздаются: первый запуск каждой игры может один раз подтормозить при компиляции шейдеров. Занятые файлы пропускаются. |
| `optimize.cleanup.undo` | Cannot be undone, and there is nothing to restore: Windows and drivers rebuild these caches. | Отменить нельзя, но и восстанавливать нечего: Windows и драйверы пересоздают эти кэши сами. |
| `optimize.cleanup.scan` | Scan | Проверить |
| `optimize.cleanup.clean` | Clean | Очистить |
| `optimize.cleanup.result` | Freed {0}. {1} files skipped (in use or protected). | Освобождено {0}. Пропущено файлов: {1} (заняты или защищены). |
| `optimize.cleanup.cat.userTemp` | Temporary files (older than 24 h) | Временные файлы (старше 24 ч) |
| `optimize.cleanup.cat.directXShaderCache` | DirectX shader cache | Кэш шейдеров DirectX |
| `optimize.cleanup.cat.nvidiaShaderCache` | NVIDIA shader cache | Кэш шейдеров NVIDIA |
| `optimize.cleanup.cat.amdShaderCache` | AMD shader cache | Кэш шейдеров AMD |
| `optimize.cleanup.cat.doCache` | Delivery Optimization cache (Windows Update) | Кэш оптимизации доставки (Центр обновления) |
| `rec.startup.many.title` | Many apps start with Windows | Много программ в автозагрузке |
| `rec.startup.many.body` | {0} programs start when you sign in | При входе запускается программ: {0} |
| `rec.startup.many.action` | Review startup apps | Открыть автозагрузку |
| `rec.startup.many.why` | Each one adds to sign-in time and uses memory in the background. Disabling is reversible. | Каждая удлиняет вход в систему и занимает память в фоне. Отключение обратимо. |
| `rec.startup.many.risk` | Only disable what you recognise. Driver tools and security software may need to start. | Отключайте только знакомое. Утилиты драйверов и защита могут быть нужны при старте. |
| `rec.cleanup.disk.title` | Caches can free space | Кэши можно очистить |
| `rec.cleanup.disk.body` | Low space on {0}: temporary files and shader caches can be emptied | Мало места на {0}: можно очистить временные файлы и кэши шейдеров |
| `rec.cleanup.disk.action` | Open cleanup | Открыть очистку |
| `rec.hags.off.title` | GPU scheduling is off | Планирование GPU выключено |
| `rec.hags.off.body` | Your GPU supports hardware-accelerated GPU scheduling | Видеокарта поддерживает аппаратное планирование GPU |
| `rec.hags.off.action` | Turn on (admin, restart) | Включить (админ, перезагрузка) |

The in-app pass added its keys in the `// --- in-app pass ---` block of `Loc.cs` (`hw.events.*`,
`optimize.visualFx.*`, `optimize.doCache.*`, `optimize.cleanup.cat.doCache`, `optimize.ui.gfx.desktop`).
No longer referenced by code (still in the table, safe to delete in a Loc cleanup): `hw.stability.openEventViewer`,
`hw.stability.openEventViewer.fail`, `optimize.ui.openPerfOptions`, `optimize.ui.openSettings`, `optimize.ui.goSystem`,
`optimize.cleanup.infoOnly`, `optimize.cleanup.openStorage`, `optimize.cleanup.cat.deliveryOptimization`.
