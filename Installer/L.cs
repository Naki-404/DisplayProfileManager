using System.Globalization;

namespace Visor.Setup;

/// <summary>Installer UI strings (EN, or RU when the Windows UI language is Russian).</summary>
public static class L
{
    private static readonly bool Ru =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    private static string T(string en, string ru) => Ru ? ru : en;

    // Setup window
    public static string SetupTitle => T("Visor Setup", "Установка Visor");
    public static string Tagline => T("Per-game display profiles for Windows.", "Профили экрана для каждой игры.");
    public static string SetupBlurb => T(
        "Installs for the current user: no admin rights and no .NET runtime needed. Your profiles in %AppData%\\Visor are never touched.",
        "Ставится для текущего пользователя: без прав администратора и без .NET. Профили в %AppData%\\Visor не затрагиваются.");
    public static string InstallLocation => T("Install location", "Папка установки");
    public static string BrowseTitle => T("Choose a folder (a \"Visor\" subfolder is created inside)",
                                          "Выберите папку (внутри будет создана папка \"Visor\")");
    public static string StartMenuShortcut => T("Start Menu shortcut", "Ярлык в меню Пуск");
    public static string DesktopShortcut => T("Desktop shortcut", "Ярлык на рабочем столе");
    public static string LaunchAfter => T("Launch Visor after install", "Запустить Visor после установки");
    public static string Cancel => T("Cancel", "Отмена");
    public static string Close => T("Close", "Закрыть");
    public static string Install => T("Install", "Установить");
    public static string Update => T("Update", "Обновить");
    public static string MadeBy => "made by Nakidev";
    public static string Installing => T("Installing…", "Установка…");
    public static string Version(string v) => T("Version ", "Версия ") + v + "  ·  Nakidev";
    public static string WillInstallTo(string p) => T("Will install to: ", "Будет установлено в: ") + p;
    public static string WillUpdate(string p, string? old) =>
        T("Will update the existing install", "Будет обновлена установленная версия") +
        (string.IsNullOrEmpty(old) ? "" : " " + old) + ": " + p;
    public static string RemoveLegacy(string where) =>
        T("Remove old Display Profile Manager", "Удалить старый Display Profile Manager") +
        (string.IsNullOrEmpty(where) ? "" : " (" + where + ")") +
        T(" — settings are kept", " — настройки сохранятся");
    public static string InstalledOk => T("Installed successfully", "Установка завершена");
    public static string UpdatedOk => T("Updated successfully", "Обновление завершено");
    public static string ThankYou => T("Thank you for installing Visor.\nEnjoy — made by Nakidev",
                                       "Спасибо, что установили Visor.\nПриятной игры — made by Nakidev");
    public static string InstallFailed => T("Install failed:\n", "Установка не удалась:\n");
    public static string Warnings => T("Finished with warnings:", "Готово, но есть предупреждения:");

    // Progress
    public static string StClosing => T("Closing running apps…", "Закрываю запущенные программы…");
    public static string StExtracting => T("Extracting files…", "Распаковка файлов…");
    public static string StInstalling => T("Installing files…", "Копирование файлов…");
    public static string StLegacy => T("Removing old Display Profile Manager…", "Удаляю старый Display Profile Manager…");
    public static string StShortcuts => T("Creating shortcuts…", "Создаю ярлыки…");
    public static string StRegister => T("Registering in Apps & Features…", "Регистрация в «Приложения и компоненты»…");
    public static string StRestore => T("Restoring display settings…", "Восстанавливаю настройки экрана…");
    public static string StAutostart => T("Removing autostart…", "Убираю автозапуск…");
    public static string StDeleting => T("Deleting application files…", "Удаляю файлы программы…");
    public static string StUnregister => T("Unregistering from Apps & Features…", "Удаляю запись из «Приложения и компоненты»…");
    public static string StDone => T("Done.", "Готово.");

    // Prompts / errors
    public static string AppsRunning(string names) => T(
        names + " is running and must be closed to continue.\n\nClose it now? It gets a few seconds to exit cleanly, then it is stopped.",
        names + " запущен(а) и будет закрыт(а) для продолжения.\n\nЗакрыть сейчас? Программе дадут несколько секунд на корректный выход, затем она будет остановлена.");
    public static string CannotClose(string name) => T(
        name + " could not be closed. Close it manually (tray icon → Exit) and try again.",
        "Не удалось закрыть " + name + ". Закройте программу вручную (значок в трее → Выход) и повторите.");
    public static string FolderNotEmpty(string dir) => T(
        "The folder\n" + dir + "\nalready contains files that are not part of a Visor installation.\nChoose an empty folder or a different location.",
        "Папка\n" + dir + "\nуже содержит файлы, не относящиеся к установке Visor.\nВыберите пустую папку или другое место.");
    public static string FolderNotEmptyShort => T("This folder contains other files — choose another location.",
                                                   "В папке есть посторонние файлы — выберите другое место.");
    public static string ProtectedFolder(string dir) => T(
        "Visor cannot be installed into\n" + dir + "\nThis is a system or personal folder. Choose a different location.",
        "Нельзя установить Visor в\n" + dir + "\nЭто системная или личная папка. Выберите другое место.");
    public static string ProtectedFolderShort => T("System or personal folder — choose another location.",
                                                   "Системная или личная папка — выберите другое место.");
    public static string InvalidPath => T("Enter a full local path, e.g. C:\\Games\\Visor.",
                                          "Укажите полный локальный путь, например C:\\Games\\Visor.");
    public static string PayloadMissing => T("This Setup has no application payload. Rebuild it with build-release.ps1.",
                                             "В этом Setup нет файлов программы. Пересоберите его через build-release.ps1.");
    public static string RolledBack(string err) => T(
        "Installation failed and was rolled back; your previous version is unchanged.\n\n" + err,
        "Установка не удалась и была отменена; прежняя версия не изменилась.\n\n" + err);
    public static string RollbackFailed(string err, string backup) => T(
        "Installation failed and the rollback was incomplete.\nYour previous files are in:\n" + backup + "\n\n" + err,
        "Установка не удалась, откат выполнен не полностью.\nПрежние файлы сохранены в:\n" + backup + "\n\n" + err);
    public static string ShortcutFailed(string which, string err) => T(
        "Could not create the " + which + " shortcut: " + err,
        "Не удалось создать ярлык (" + which + "): " + err);
    public static string ShortcutExists(string which, string lnk) => T(
        "The " + which + " shortcut was not created: " + lnk + " already exists and belongs to another program.",
        "Ярлык (" + which + ") не создан: " + lnk + " уже существует и принадлежит другой программе.");
    public static string RegisterFailed(string err) => T(
        "Could not register in Apps & Features (" + err + "). You can still uninstall with Uninstall.exe in the install folder.",
        "Не удалось зарегистрировать программу в «Приложения и компоненты» (" + err + "). Удалить можно через Uninstall.exe в папке установки.");
    public static string LegacyLeftovers(string dir) => T(
        "Some files of the old Display Profile Manager were left in " + dir,
        "Часть файлов старого Display Profile Manager осталась в " + dir);
    public static string LegacySkipped(string dir) => T(
        "Old Display Profile Manager files in " + dir + " were not removed (unsafe location). Delete them manually.",
        "Файлы старого Display Profile Manager в " + dir + " не удалены (небезопасное место). Удалите их вручную.");
    public static string UninstallIncomplete(string files) => T(
        "Some files could not be deleted (in use?):\n" + files + "\n\nClose Visor and run the uninstaller again.",
        "Не удалось удалить некоторые файлы (заняты?):\n" + files + "\n\nЗакройте Visor и запустите удаление ещё раз.");

    // Uninstall window
    public static string UninstallTitle => T("Uninstall Visor", "Удаление Visor");
    public static string UninstallHeader => T("Remove Visor?", "Удалить Visor?");
    public static string UninstallBlurb => T(
        "Visor is closed if it is running and restores your display settings, then the app is removed. Your profiles in %AppData%\\Visor are kept.",
        "Visor будет закрыт и вернёт настройки экрана, затем программа будет удалена. Профили в %AppData%\\Visor сохранятся.");
    public static string Uninstall => T("Uninstall", "Удалить");
    public static string NotFound => T("(not found)", "(не найдено)");
    public static string NothingInstalled => T("No Visor installation was found.", "Установленный Visor не найден.");
    public static string Removing => T("Removing…", "Удаление…");
    public static string Uninstalled => T("Uninstalled", "Удалено");
    public static string UninstallThanks => T("Thanks for using Visor — made by Nakidev",
                                              "Спасибо, что пользовались Visor — made by Nakidev");
    public static string UninstallFailed => T("Uninstall failed:\n", "Удаление не удалось:\n");
}
