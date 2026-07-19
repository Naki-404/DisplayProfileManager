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
        ["btn.preview"] = ("Preview 5s", "Превью 5с"),
        ["btn.scan"] = ("Scan games", "Сканировать"),
        ["btn.addManual"] = ("Add manually", "Добавить вручную"),
        ["btn.restore"] = ("Restore my games", "Восстановить игры"),
        ["btn.dup"] = ("Dup", "Копия"),
        ["btn.delete"] = ("Delete", "Удалить"),
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
        ["tray.reset"] = ("Reset settings", "Сброс настроек"),
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
        ["toast.game"] = ("Profile applied", "Профиль применён"),
        ["toast.preview"] = ("Preview — reverting in 5s…", "Превью — откат через 5с…"),
        ["toast.previewDone"] = ("Preview ended", "Превью завершено"),
        ["toast.imported"] = ("Profiles imported", "Профили импортированы"),
        ["toast.exported"] = ("Profiles exported", "Профили экспортированы"),
        ["toast.saved"] = ("Saved", "Сохранено"),
        ["confirm.delete"] = ("Delete this profile?", "Удалить этот профиль?"),
        ["display"] = ("Display", "Дисплей"),
        ["profile"] = ("Profile", "Профиль"),
        ["enabled"] = ("Enabled", "Включено"),
        ["alreadyRunning"] = ("Display Profile Manager is already running.", "Display Profile Manager уже запущен."),
    };

    public static string Locale => _locale;

    public static event Action? Changed;

    public static void SetLocale(string? locale)
    {
        var next = string.Equals(locale, "ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
        if (_locale == next) return;
        _locale = next;
        Changed?.Invoke();
    }

    public static string T(string key)
    {
        if (!Map.TryGetValue(key, out var pair)) return key;
        return _locale == "ru" ? pair.Ru : pair.En;
    }
}
