# PC tweaks: startup apps, Ultimate plan, GPU scheduling, cache cleanup

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

Wiring (UI side):

```csharp
var pc = PcTweaks.Create(Config.ConfigDirectory, broker, setPlan: Display.SetPowerPlan);
pc.Register(safeActions);                  // hags.on / hags.off / ultimatePlan.on + journaled startup toggles
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
| `MachineRun32` | HKLM `WOW6432Node\…\Run` | HKLM `StartupApproved\Run32` | read-only (not in the helper allowlist) |
| `CommonStartupFolder` | `shell:common startup` | HKLM `StartupApproved\StartupFolder` | read-only (not in the helper allowlist) |

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
- Visor **never deletes** the created scheme. Users can remove it in Control Panel → Power Options.
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

**Honest impact:** the effect depends on the GPU and driver, and there is often no measurable change.
It needs a restart. Some capture tools and older titles behave worse with it on.

## 4. Cache cleanup — `CleanupService` (not journaled)

| Category | Roots | Filter |
|---|---|---|
| `UserTemp` | `%TEMP%` | only items whose newer of creation / last-write time is > 24 h old |
| `DirectXShaderCache` | `%LocalAppData%\D3DSCache` | all |
| `NvidiaShaderCache` | `%LocalAppData%\NVIDIA\DXCache`, `GLCache`, `%ProgramData%\NVIDIA Corporation\NV_Cache` (whatever is accessible) | all |
| `AmdShaderCache` | `%LocalAppData%\AMD\DxCache`, `DxcCache`, `GLCache` | all |
| `DeliveryOptimization` | `%windir%\ServiceProfiles\NetworkService\…\DeliveryOptimization\Cache` | **info only** (owned by NetworkService, admin). Point users to Settings → Storage |

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

## 7. Loc keys the UI needs

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
| `optimize.startup.readOnly` | Change this one in Task Manager → Startup apps. | Это меняется в Диспетчере задач → Автозагрузка. |
| `optimize.startup.enable` | Enable | Включить |
| `optimize.startup.disable` | Disable | Отключить |
| `optimize.ultimatePlan.title` | Ultimate Performance power plan | Схема «Максимальная производительность» |
| `optimize.ultimatePlan.why` | Windows' hidden plan for desktops: removes small CPU and device power-saving delays. Visor already switches plans while a game profile runs, so this mainly matters outside games. | Скрытая схема Windows для ПК: убирает мелкие задержки энергосбережения процессора и устройств. Visor и так переключает схему на время игры, поэтому это важно в основном вне игр. |
| `optimize.ultimatePlan.risk` | More heat, fan noise and power draw all the time. Not for laptops on battery. No FPS promise. | Больше нагрев, шум и энергопотребление всё время. Не для ноутбука от батареи. Прирост FPS не обещается. |
| `optimize.ultimatePlan.undo` | Undo switches back to your previous plan. The created plan stays in the list (remove it in Power Options if you like). | Отмена возвращает прежнюю схему. Созданная схема остаётся в списке (её можно удалить в «Электропитании»). |
| `optimize.ultimatePlan.missing` | Not installed. Create it first (one UAC prompt). | Схема не установлена. Сначала создайте её (один запрос UAC). |
| `optimize.ultimatePlan.create` | Create plan | Создать схему |
| `optimize.ultimatePlan.created` | Plan created | Схема создана |
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
| `optimize.cleanup.cat.deliveryOptimization` | Delivery Optimization (Windows Update). Clear it in Settings → System → Storage → Temporary files. | Оптимизация доставки (Центр обновления). Очищается в Параметры → Система → Память → Временные файлы. |
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

The existing `optimize.hags.why` text ("Visor never elevates") is now outdated. It should say the
change goes through one UAC prompt.
