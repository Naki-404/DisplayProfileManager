namespace DisplayProfileManager.Services;

/// <summary>Tiny EN/RU string table. Call Loc.SetLocale then Loc.T("key").</summary>
public static class Loc
{
    private static string _locale = "en";
    private static readonly Dictionary<string, (string En, string Ru)> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["app.name"] = ("Display Profile Manager", "Display Profile Manager"),
        ["status.monitoring"] = ("Monitoring", "Мониторинг"),
        ["status.paused"] = ("Paused", "Пауза"),
        ["status.idle"] = ("Idle (defaults)", "Простой (по умолчанию)"),
        ["btn.apply"] = ("Apply", "Применить"),
        ["btn.save"] = ("Save", "Сохранить"),
        ["btn.reset"] = ("Reset settings", "Сброс настроек"),
        ["btn.reset.global"] = ("Reset Global defaults", "Сброс Global defaults"),
        ["btn.reset.global.hint"] = ("Rewrites Global defaults to factory values and restores the display. For a quick panic fix use Emergency Restore in the header.", "Перезаписывает Global defaults на заводские и восстанавливает экран. Для быстрого сброса цвета — Emergency Restore в шапке."),
        ["btn.reset.tip"] = ("Restore factory display settings (native resolution + neutral color).", "Вернуть заводские настройки дисплея (родное разрешение + нейтральный цвет)."),
        ["btn.preview"] = ("Preview", "Превью"),
        ["btn.compareAb"] = ("A/B", "A/B"),
        ["btn.scan"] = ("Scan", "Скан"),
        ["btn.addManual"] = ("Add", "Добавить"),
        ["btn.restore"] = ("Restore games", "Восстановить"),
        ["btn.dup"] = ("Dup", "Копия"),
        ["btn.delete"] = ("Delete", "Удалить"),
        ["btn.add"] = ("Add", "Добавить"),
        ["btn.settings"] = ("Settings", "Настройки"),
        ["btn.import"] = ("Import…", "Импорт…"),
        ["btn.export"] = ("Export…", "Экспорт…"),
        ["btn.next"] = ("Next", "Далее"),
        ["btn.back"] = ("Back", "Назад"),
        ["btn.finish"] = ("Finish", "Готово"),
        ["btn.cancel"] = ("Cancel", "Отмена"),
        ["btn.ok"] = ("OK", "OK"),
        ["tab.profiles"] = ("Profiles", "Профили"),
        ["tab.presets"] = ("Presets", "Пресеты"),
        ["tab.global"] = ("Global", "Общие"),
        ["tab.log"] = ("Log", "Журнал"),
        ["search.placeholder"] = ("Search games…", "Поиск игр…"),
        ["active.none"] = ("No game active", "Игра не активна"),
        ["active.prefix"] = ("Active:", "Активно:"),
        ["tray.open"] = ("Open", "Открыть"),
        ["tray.pause"] = ("Pause monitoring", "Пауза мониторинга"),
        ["tray.resume"] = ("Resume monitoring", "Возобновить"),
        ["tray.reset"] = ("Emergency Restore", "Аварийный сброс"),
        ["tray.overlay"] = ("Live overlay", "Live оверлей"),
        ["tray.exit"] = ("Exit", "Выход"),
        ["tray.presets"] = ("Presets", "Пресеты"),
        ["tray.none"] = ("(no active game)", "(нет активной игры)"),
        ["setup.title"] = ("Welcome", "Добро пожаловать"),
        ["setup.subtitle"] = ("Quick setup — you can change this later in Settings.", "Быстрая настройка — потом можно изменить в Параметрах."),
        ["setup.language"] = ("Language", "Язык"),
        ["setup.theme"] = ("Theme", "Тема"),
        ["setup.theme.dark"] = ("Dark pink", "Тёмная розовая"),
        ["setup.theme.light"] = ("Light", "Светлая"),
        ["setup.theme.custom"] = ("Custom", "Своя"),
        ["setup.editPalette"] = ("Edit palette…", "Настроить палитру…"),
        ["setup.accent"] = ("Accent color", "Акцент"),
        ["setup.background"] = ("Background", "Фон"),
        ["setup.autostart"] = ("Start with Windows", "Запуск с Windows"),
        ["setup.startMin"] = ("Start minimized to tray", "Сразу в трей"),
        ["setup.notify"] = ("Toast when a game starts", "Уведомление при старте игры"),
        ["setup.backup"] = ("Backup config before each save", "Бэкап конфига перед сохранением"),
        ["setup.trayClose"] = ("Close window → stay in tray", "Закрытие окна → остаться в трее"),
        ["setup.monitor"] = ("Default monitor for resolution", "Монитор для разрешения"),
        ["setup.monitor.primary"] = ("Primary", "Основной"),
        ["settings.title"] = ("Settings", "Параметры"),
        ["settings.appearance"] = ("Appearance", "Внешний вид"),
        ["settings.behavior"] = ("Behavior", "Поведение"),
        ["settings.data"] = ("Data", "Данные"),
        ["settings.saved"] = ("Settings saved", "Настройки сохранены"),
        ["settings.confirmDelete"] = ("Confirm before delete", "Подтверждать удаление"),
        ["settings.showActive"] = ("Show active profile in header", "Показывать активный профиль в шапке"),
        ["toast.game"] = ("Profile applied", "Профиль применён"),
        ["toast.snapshot"] = ("Snapshot saved", "Снимок сохранён"),
        ["toast.emergency"] = ("Emergency Restore done", "Аварийный сброс выполнен"),
        ["toast.config.recovered"] = ("Config recovered", "Конфиг восстановлен"),
        ["toast.preview"] = ("Color preview applied — use A/B to compare with factory", "Превью цвета — A/B сравнивает с заводским"),
        ["toast.previewDone"] = ("Preview ended", "Превью завершено"),
        ["toast.ab.preview"] = ("A/B: Preview", "A/B: Превью"),
        ["toast.ab.factory"] = ("A/B: Factory", "A/B: Заводской"),
        ["toast.crash.restored"] = ("Restored after crash", "Восстановлено после сбоя"),
        ["toast.health"] = ("GPU: {0}", "GPU: {0}"),
        ["toast.imported"] = ("Profiles imported", "Профили импортированы"),
        ["toast.exported"] = ("Profiles exported", "Профили экспортированы"),
        ["toast.saved"] = ("Saved", "Сохранено"),
        ["confirm.delete"] = ("Delete this profile?", "Удалить этот профиль?"),
        ["hotkey.compareAb"] = ("A/B compare", "Сравнить A/B"),
        ["display"] = ("Display", "Дисплей"),
        ["profile"] = ("Profile", "Профиль"),
        ["enabled"] = ("Enabled", "Включено"),
        ["alreadyRunning"] = ("Display Profile Manager is already running.", "Display Profile Manager уже запущен."),

        // Profile editor / color
        ["color.backend"] = ("Color backend", "Способ цвета"),
        ["color.lock"] = ("Lock color while game is running", "Удерживать цвет, пока игра запущена"),
        ["color.lock.tip"] = ("Re-applies gamma every 0.5–2s so the game cannot reset it.", "Периодически заново ставит гамму, чтобы игра её не сбрасывала."),
        ["color.backend.tip"] = (
            "Left: NVIDIA/AMD Control Panel–style B/C/G + Digital Vibrance. Right: Low Level — full RivaTuner B/C/G ramp.",
            "Слева: B/C/G как в панели NVIDIA/AMD + Digital Vibrance. Справа: Low Level — полная рампа RivaTuner B/C/G."),
        ["color.apply"] = ("Apply color with this profile", "Применять цвет с этим профилем"),
        ["color.apply.tip"] = ("When enabled, brightness / contrast / gamma / vibrance from this profile are applied when the game starts.", "Если включено, яркость / контраст / гамма / vibrance из профиля применяются при старте игры."),
        ["color.brightness.tip"] = ("RivaTuner Brightness (−125..125). Added in 8-bit space before contrast/gamma.", "Яркость RivaTuner (−125..125). Добавляется в 8-битном пространстве до контраста/гаммы."),
        ["color.vibrance.tip"] = ("Color saturation. On NVIDIA/AMD uses driver vibrance; ignored by the Low Level ramp.", "Насыщенность. На NVIDIA/AMD — vibrance драйвера; на рампу Low Level не влияет."),
        ["color.shadow.tip"] = ("Shadow Boost (0–100): lifts dark areas while preserving highlights. Low Level only.", "Shadow Boost (0–100): поднимает тени, сохраняя света. Только Low Level."),
        ["restore.mode.tip"] = ("What to restore when this game exits: previous PC snapshot, Global Defaults, or leave as-is.", "Что восстановить при выходе из игры: снимок ПК, Global Defaults, или ничего."),
        ["restore.mode.lbl"] = ("On game exit", "При выходе из игры"),
        ["btn.emergency"] = ("Emergency Restore", "Аварийный сброс"),
        ["btn.emergency.tip"] = ("Restores display, color, power, and session settings to a safe state.", "Восстанавливает дисплей, цвет, питание и сессию в безопасное состояние."),
        ["display.res.tip"] = ("Switch the selected monitor to this resolution while the game runs, then restore on exit.", "Переключает выбранный монитор на это разрешение на время игры и возвращает назад при выходе."),
        ["display.power.tip"] = ("Switch Windows power plan (High performance / Balanced) while the game runs.", "Переключает схему электропитания Windows (высокая производительность / сбалансированная) на время игры."),
        ["companions.tip"] = ("Extra programs launched with the game (optional args, e.g. Overwolf -launchapp …). Closed when the game exits if configured.", "Доп. программы при старте игры (можно указать аргументы, напр. Overwolf -launchapp …). При выходе могут закрываться."),
        ["btn.apply.tip"] = ("Apply to the display for the current tab. Settings autosave as you edit — no Save button.", "Применить к экрану для текущей вкладки. Настройки сохраняются сами при изменении — кнопки Save нет."),
        ["toast.applied"] = ("Applied", "Применено"),
        ["toast.preset.hotkey"] = ("Preset hotkey applied", "Пресет по хоткею применён"),
        ["toast.global.kept.preset"] = ("Global saved — active preset kept on screen", "Global сохранён — активный пресет на экране не тронут"),
        ["overlay.title"] = ("Live color", "Цвет live"),
        ["overlay.live.hint"] = ("Sliders apply to the display instantly — no Save needed.", "Ползунки сразу меняют картинку на экране."),
        ["overlay.apply"] = ("Save to Profile", "В профиль"),
        ["overlay.save.preset"] = ("Save as Preset", "Как пресет"),
        ["overlay.update.preset"] = ("Update Preset", "Обновить пресет"),
        ["overlay.reset"] = ("Reset", "Сброс"),
        ["overlay.emergency"] = ("Emergency", "Авария"),
        ["overlay.preset"] = ("Preset: {0}", "Пресет: {0}"),
        ["overlay.preset.name"] = ("Preset name", "Имя пресета"),
        ["overlay.need.profile"] = ("Select or start a game profile first.", "Сначала выберите или запустите профиль игры."),
        ["overlay.collapse"] = ("Minimize to pill", "Свернуть в таб"),
        ["overlay.hide"] = ("Hide overlay", "Скрыть оверлей"),
        ["overlay.mini"] = ("DPM", "DPM"),
        ["overlay.no.game"] = ("No active game — preview only", "Нет активной игры — только превью"),
        ["overlay.open"] = ("Live overlay", "Live оверлей"),
        ["overlay.auto"] = ("Show overlay when a game starts", "Показывать оверлей при старте игры"),
        ["overlay.auto.hint"] = ("Compact topmost panel with live color sliders over the game.", "Компактная панель поверх игры с live-ползунками цвета."),
        ["overlay.panelOpacity"] = ("Panel opacity", "Прозрачность панели"),
        ["overlay.applied"] = ("Color saved", "Цвет сохранён"),
        ["btn.overlay"] = ("Live overlay", "Live оверлей"),
        ["btn.overlay.tip"] = ("Floating panel over the game — sliders apply color live. Main window hides to tray until you close the overlay.", "Панель поверх игры — ползунки сразу меняют цвет. Главное окно уходит в трей, пока оверлей открыт."),
        ["presets.export.tip"] = ("Share this game’s preset pack as a file others can import.", "Сохранить набор пресетов этой игры в файл для передачи."),
        ["presets.import.tip"] = ("Load a preset pack file into the selected game.", "Загрузить файл с пресетами в выбранную игру."),
        ["global.autostart.tip"] = ("Create a scheduled task so the app starts when you sign in to Windows.", "Создаёт задачу планировщика, чтобы приложение запускалось при входе в Windows."),
        ["global.startmin.tip"] = ("Start hidden in the system tray instead of showing the main window.", "Запускать свёрнутым в трей, без показа главного окна."),

        // Session extras
        ["session.title"] = ("Session extras", "Дополнительно на сессию"),
        ["session.sub"] = ("System-only tweaks (no game injection)", "Только системные настройки (без вмешательства в игру)"),
        ["session.deferred"] = ("Deferred re-apply: {0} s", "Повторное применение через: {0} с"),
        ["session.deferred.tip"] = ("Wait N seconds after game start, then apply display/color again. Helps when a launcher overwrites settings.", "Подождать N секунд после старта игры и снова применить цвет/разрешение. Полезно, если лаунчер перебивает настройки."),
        ["session.quiet"] = ("Quiet notifications", "Тихие уведомления"),
        ["session.quiet.tip"] = ("Turns off Windows toast notifications while the game runs (Focus Assist–style). Restored when the game exits.", "Отключает всплывающие уведомления Windows на время игры (как Focus Assist). После выхода из игры возвращает обратно."),
        ["session.night"] = ("Disable Night Light", "Отключить Night Light"),
        ["session.night.tip"] = ("Tries to turn off Windows blue-light filter (Night Light) so colors stay predictable in dark scenes.", "Пытается выключить фильтр синего света Windows (Night Light), чтобы картинка в тенях была предсказуемой."),
        ["session.hdr"] = ("Disable Auto HDR", "Отключить Auto HDR"),
        ["session.hdr.tip"] = ("Turns off Windows Auto HDR for games so SDR color presets are not altered.", "Отключает Auto HDR Windows для игр, чтобы SDR-пресеты цвета не искажались."),
        ["session.primary"] = ("Primary monitor only", "Только основной монитор"),
        ["session.primary.tip"] = ("Temporarily disables extra monitors while the game runs. Restored on exit.", "Временно отключает дополнительные мониторы на время игры. После выхода восстанавливает."),
        ["session.bright"] = ("Set monitor brightness (DDC/CI)", "Яркость монитора (DDC/CI)"),
        ["session.bright.lbl"] = ("Panel brightness: {0}%", "Яркость панели: {0}%"),
        ["session.bright.tip"] = ("Changes the monitor’s own backlight via DDC/CI (not gamma). Needs a compatible cable/monitor.", "Меняет подсветку самого монитора через DDC/CI (не гамму). Нужен поддерживаемый монитор/кабель."),
        ["session.audio"] = ("Switch default audio device", "Сменить устройство звука"),
        ["session.audio.tip"] = ("Switches the Windows default playback device for this profile (e.g. headset). Restored on exit.", "Переключает устройство воспроизведения Windows по умолчанию (например наушники). После выхода возвращает."),
        ["session.scaling"] = ("Scaling mode", "Масштабирование"),
        ["session.scaling.tip"] = ("How a non-native resolution is shown: default, stretch, or center (letterbox).", "Как показывать неродное разрешение: по умолчанию, растянуть или по центру."),
        ["presets.export"] = ("Export", "Экспорт"),
        ["presets.import"] = ("Import", "Импорт"),
        ["unsaved.title"] = ("Unsaved changes", "Несохранённые изменения"),
        ["unsaved.message"] = ("You have unsaved changes. Save them before continuing?", "Есть несохранённые изменения. Сохранить перед продолжением?"),
        ["unsaved.save"] = ("Save", "Сохранить"),
        ["unsaved.discard"] = ("Don't save", "Не сохранять"),
        ["unsaved.cancel"] = ("Cancel", "Отмена"),
    };

    public static string Locale => _locale;

    public static event Action? Changed;

    public static void SetLocale(string? locale)
    {
        var next = string.Equals(locale, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        bool changed = _locale != next;
        _locale = next;
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>Format Loc.T key that contains {0} placeholders.</summary>
    public static string Tf(string key, params object[] args)
    {
        try { return string.Format(T(key), args); }
        catch { return T(key); }
    }

    public static string T(string key)
    {
        if (!Map.TryGetValue(key, out var pair)) return key;
        return _locale == "ru" ? pair.Ru : pair.En;
    }
}
