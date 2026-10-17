using System.Windows;
using Visor.Services;

namespace Visor;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\Visor.SingleInstance";
    /// <summary>Signalled by the installer to request a clean exit of the running instance.</summary>
    private const string ExitRequestEventName = @"Local\Visor.ExitRequest";

    public static AppServices Services { get; private set; } = null!;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Font? _trayFont;
    private System.Drawing.Font? _trayFontBold;
    private System.Drawing.Icon? _trayIcon;
    private MainWindow? _main;
    private GameOverlayWindow? _overlay;
    private bool _exitRequested;
    private bool _cleanedUp;
    private bool _mainHiddenForOverlay;
    private Mutex? _mutex;
    private EventWaitHandle? _exitRequestEvent;
    private RegisteredWaitHandle? _exitRequestWait;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryHandleCliArgs(e.Args))
            return;

        DispatcherUnhandledException += (_, args) =>
        {
            var ex = args.Exception;
            if (IsFatal(ex))
            {
                // Process state is unreliable — let it crash; the session snapshot on disk is
                // restored on the next start.
                AppLog.Error(ex, "Fatal UI exception");
                args.Handled = false;
                return;
            }

            AppLog.Error(ex, "UI exception");
            args.Handled = true;
            try { ThemedDialog.Show(null, "Something went wrong:\n" + ex.Message); }
            catch (Exception dex) { AppLog.Warn(dex, "Error dialog"); }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLog.Error(ex, "Unhandled");
            else
                AppLog.Error("Unhandled: " + args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error(args.Exception, "Task exception");
            args.SetObserved();
        };
        // Logoff / shutdown is not a crash: restore the game session synchronously and exit cleanly.
        // Windows allows ~5 s here, so this is the "quick" path: no companion stop, no queue
        // drains (a pending FPS-cap undo is persisted and retried next start), short NvAPI wait.
        SessionEnding += (_, args) =>
        {
            AppLog.Info($"Windows session ending ({args.ReasonSessionEnding}) — restoring.");
            _exitRequested = true;
            CleanupForExit(stopCompanions: false);
        };

        try
        {
            _mutex = new Mutex(true, SingleInstanceMutexName, out bool created);
            if (!created)
            {
                bool silent = e.Args.Any(a =>
                    a.Equals(AutostartService.StartupArgument, StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/minimized", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
                if (!silent)
                {
                    Loc.SetLocale("en");
                    ThemedDialog.Show(null, Loc.T("alreadyRunning"));
                }
                try { _mutex.Dispose(); } catch (ObjectDisposedException) { }
                _mutex = null;
                Shutdown();     // a second instance writes nothing to the shared log
                return;
            }

            // First line of this run — only once we own the single-instance mutex (the default log
            // path is the config folder AppServices uses too).
            AppLog.StartupBanner();

            StartExitRequestListener();
            Services = new AppServices();
            var ui = Services.Config.Current.Ui ?? new Models.UiPreferences();
            Loc.SetLocale(ui.Locale);
            ThemeService.Apply(ui);
            UiSound.ApplyFromConfig(ui);

            bool startMinimized = e.Args.Any(a =>
                a.Equals(AutostartService.StartupArgument, StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

            // First-run wizard (new config only)
            bool wizardRan = false;
            if (!ui.SetupCompleted && !startMinimized)
            {
                wizardRan = true;
                try
                {
                    var wiz = new SetupWizardWindow();
                    wiz.ShowDialog();
                    ui = Services.Config.Current.Ui ?? ui;
                    Loc.SetLocale(ui.Locale);
                    ThemeService.Apply(ui);
                    UiSound.ApplyFromConfig(ui);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Setup wizard failed: " + ex.Message);
                    ui.SetupCompleted = true;
                    Services.Config.Current.Ui = ui;
                    Services.Config.Save(Services.Config.Current);
                }
            }

            // Right after the wizard the user expects the window, even if they ticked "start minimized".
            bool showWindow = !startMinimized && (wizardRan || !Services.Config.Current.StartMinimized);

            // Splash only covers real start-up work (main window construction + first render).
            BootSplashWindow? splash = null;
            if (showWindow)
            {
                try
                {
                    splash = new BootSplashWindow();
                    splash.Show();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Boot splash failed: " + ex.Message);
                    splash = null;
                }
            }

            _main = new MainWindow();
            MainWindow = _main;
            try
            {
                _overlay = new GameOverlayWindow();
                _overlay.ApplyLocalization();
            }
            catch (Exception ex)
            {
                AppLog.Error("Overlay create failed: " + ex);
                _overlay = null;
            }

            InitTray();
            RebuildTrayMenu();

            // GPU vendor detection (WMI) runs in the background since AppServices started it; if
            // the windows were built before it finished they show "GPU" — relabel them once.
            bool gpuKnownAtBuild = GpuVendorDetect.IsDetected;
            GpuVendorDetect.WhenDetected.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    AppLog.Info("Startup health: GPU " + GpuVendorDetect.DriverLabel
                                + (Services.CrashRestored ? ", restored after crash" : ""));
                    if (gpuKnownAtBuild || _cleanedUp) return;
                    _main?.ApplyLocalization();
                    _overlay?.ApplyLocalization();
                }
                catch (Exception ex)
                {
                    AppLog.Warn(ex, "GPU label refresh");
                }
            }), TaskScheduler.Default);

            _main.SourceInitialized += (_, _) =>
            {
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(_main);
                    Services.Hotkeys.Attach(helper.Handle);
                    Services.Hotkeys.RegisterFromConfig(
                        Services.Config.Current,
                        Services.Monitor.CurrentProfile,
                        _main.HotkeyPresetFallback);
                    NotifyHotkeyFailures();
                    // Raised on the UI thread (queued by the hotkey service, never inside the hook).
                    Services.Hotkeys.HotkeyPressed += OnHotkeyPressed;
                }
                catch (Exception ex)
                {
                    AppLog.Error("Hotkey attach failed: " + ex);
                }
            };

            Services.Config.ConfigChanged += () =>
                _main.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        var u = Services.Config.Current.Ui;
                        if (u != null)
                        {
                            Loc.SetLocale(u.Locale);
                            ThemeService.Apply(u);
                        }
                        Services.Hotkeys.RegisterFromConfig(
                            Services.Config.Current,
                            Services.Monitor.CurrentProfile,
                            _main.HotkeyPresetFallback);
                        _main.ReloadFromConfig();
                        _main.ApplyLocalization();
                        _overlay?.ApplyLocalization();
                        RebuildTrayMenu();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error(ex, "ConfigChanged UI");
                    }
                });

            Services.Monitor.ActiveProfileChanged += profile =>
                _main.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        // Don't leave Magnification zoom on when a game arms.
                        if (profile != null)
                            Services.Zoom.Off();

                        Services.Hotkeys.RegisterFromConfig(
                            Services.Config.Current,
                            profile,
                            _main.HotkeyPresetFallback);
                        NotifyHotkeyFailures();
                        _main.UpdateActiveHeader(profile);
                        _main.FocusPresetsForActiveOrSelectedGame(profile);
                        RebuildTrayMenu();
                        if (profile != null && (Services.Config.Current.Ui?.OverlayAutoShowOnGame ?? false))
                            ShowGameOverlay(sync: true);
                        if (profile != null && (Services.Config.Current.Ui?.NotifyOnGameStart ?? true))
                        {
                            var msg = $"{Loc.T("toast.game")}: {profile.Name}";
                            string? presetId = Services.Monitor.ActivePresetId;
                            if (presetId != null)
                            {
                                var pr = profile.Presets?.FirstOrDefault(p => p.Id == presetId);
                                if (pr != null) msg += "\n" + Loc.Tf("toast.color", pr.Name);
                            }
                            var detail = Services.Monitor.LastApplyToastDetail;
                            if (!string.IsNullOrWhiteSpace(detail))
                                msg += "\n" + detail;
                            else if (Services.Monitor.SnapshotActive)
                                msg += "\n" + Loc.T("toast.snapshot");
                            _main.ShowToast(msg);
                            if (!string.IsNullOrWhiteSpace(detail)
                                && detail.Contains("fail", StringComparison.OrdinalIgnoreCase))
                                _main.ShowToast(detail);
                            try
                            {
                                _tray?.ShowBalloonTip(2800, Loc.T("app.name"), msg.Replace("\n", " · "),
                                    System.Windows.Forms.ToolTipIcon.Info);
                            }
                            catch (Exception ex)
                            {
                                AppLog.Warn(ex, "Tray balloon tip");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error(ex, "ActiveProfileChanged UI");
                    }
                });

            Services.Monitor.StatusChanged += status =>
                _main.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _main.SetStatus(status);
                        UpdateTrayTooltip();
                        var detail = Services.Monitor.LastApplyToastDetail;
                        if (!string.IsNullOrWhiteSpace(detail)
                            && detail.Contains("fail", StringComparison.OrdinalIgnoreCase)
                            && status.Contains("fail", StringComparison.OrdinalIgnoreCase))
                            _main.ShowToast(detail);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Warn(ex, "StatusChanged UI");
                    }
                });

            // Start monitoring after the window exists — avoids applying while UI is half-built.
            // Monitor.Start is cheap now (one process snapshot; WMI watchers connect off-thread).
            _main.Loaded += (_, _) =>
            {
                try { Services.Monitor.Start(); }
                catch (Exception ex) { AppLog.Error("Monitor start failed: " + ex.Message); }

                try
                {
                    if (Services.CrashRestored)
                        _main.ShowToast(Loc.T("toast.crash.restored"));

                    if (!string.IsNullOrWhiteSpace(Services.Config.LastRecoveryMessage))
                    {
                        ThemedDialog.Show(_main, Services.Config.LastRecoveryMessage);
                        _main.ShowToast(Loc.T("toast.config.recovered"));
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("Startup notices: " + ex.Message);
                }

                // Non-critical work waits until the first frame is on screen.
                _main.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                {
                    if (_cleanedUp) return;
                    SetupAutostart();
                    RunConflictCheckFireAndForget();
                    // Hardware inventory (WMI) + crash-event scan on the thread pool; My PC / Home read
                    // the cached results and never block on them.
                    try { Services.BeginHardwareProbes(); }
                    catch (Exception ex) { AppLog.Warn(ex, "Hardware probes"); }
                });
            };

            if (!showWindow)
            {
                _main.ShowInTaskbar = false;
                _main.WindowState = WindowState.Minimized;
                _main.Show();
                _main.Hide();
            }
            else
            {
                if (splash != null)
                {
                    // Hide (not Close): the splash closes itself when its own animation ends.
                    _main.ContentRendered += (_, _) =>
                    {
                        try { splash.Hide(); }
                        catch (InvalidOperationException) { /* already closed itself */ }
                    };
                }
                _main.ShowInTaskbar = true;
                _main.WindowState = WindowState.Normal;
                _main.ShowWithFade();
                UiSound.PlayWelcome(delayMs: 320);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed: " + ex);
            try { ThemedDialog.Show(null, "Startup failed:\n" + ex.Message); }
            catch (Exception dex) { AppLog.Warn(dex, "Startup error dialog"); }
            ReleaseMutex();
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Covers every shutdown path (tray Exit, logoff, Application.Shutdown from elsewhere).
        CleanupForExit(stopCompanions: true);
        base.OnExit(e);
    }

    private static bool IsFatal(Exception ex) =>
        ex is OutOfMemoryException or AccessViolationException or StackOverflowException
            or InvalidProgramException or System.Runtime.InteropServices.SEHException
            or BadImageFormatException;

    /// <summary>Hotkey actions arrive here on the UI thread.</summary>
    private void OnHotkeyPressed(string action)
    {
        if (_main == null) return;
        if (action == "toggleZoom")
        {
            if (!Services.Zoom.IsAvailable)
                _main.ShowToast(Loc.T("toast.zoom.unavailable"));
            else
            {
                bool on = Services.Zoom.Toggle();
                _main.ShowToast(on
                    ? Loc.Tf("toast.zoom.on", (int)Services.Zoom.Factor)
                    : Loc.T("toast.zoom.off"));
            }
            return;
        }

        Services.Monitor.HandleHotkey(action);
        if ((action.StartsWith("preset:", StringComparison.OrdinalIgnoreCase) || action is "nextPreset" or "previousPreset")
            && !string.IsNullOrWhiteSpace(Services.Monitor.LastPresetHotkeyName))
            _main.ShowToast($"{Loc.T("toast.preset.hotkey")}: {Services.Monitor.LastPresetHotkeyName}");
        else if (action == "compareAb")
            _main.ShowToast(Loc.T("toast.hotkey.ab"));
        else if (action == "emergencyRestore")
        {
            Services.Zoom.Off();
            _main.ShowToast(Loc.T("toast.emergency"));
        }
    }

    /// <summary>
    /// Run key (HKCU) when enabled; one-time removal of the old scheduled task after the rename.
    /// Runs after the first frame (registry write; the schtasks migration is on the thread pool).
    /// </summary>
    private static void SetupAutostart()
    {
        try
        {
            // Once per config folder (marker file) — not on every launch, never blocking, never elevated.
            var configDir = Services.Config.ConfigDirectory;
            Task.Run(() =>
            {
                try { AutostartService.MigrateLegacyOnce(configDir); }
                catch (Exception ex) { AppLog.Warn(ex, "Legacy autostart cleanup"); }
            });

            if (Services.Config.Current.StartWithWindows)
            {
                var exe = Environment.ProcessPath ?? "";
                if (!string.IsNullOrWhiteSpace(exe) && !AutostartService.SetEnabled(true, exe))
                    AppLog.Warn("Start with Windows could not be enabled.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Autostart setup failed: " + ex.Message);
        }
    }

    /// <summary>Fire-and-forget entry point (avoids CS4014 without relying on discard semantics inside a lambda).</summary>
    private async void RunConflictCheckFireAndForget() => await CheckConflictsOnceAsync();

    /// <summary>Best-effort, one-shot check for other gamma/color tools or Night Light at startup.</summary>
    private async Task CheckConflictsOnceAsync()
    {
        try
        {
            var result = await Task.Run(ConflictDetector.Scan).ConfigureAwait(false);
            var msg = ConflictDetector.Describe(result);
            if (!string.IsNullOrWhiteSpace(msg) && _main != null)
            {
#pragma warning disable CS4014 // Fire-and-forget UI marshal — nothing to await here.
                _main.Dispatcher.BeginInvoke(() => { _main.ShowToast(msg); });
#pragma warning restore CS4014
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Conflict check failed: " + ex.Message);
        }
    }


    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private const int AttachParentProcess = -1;

    /// <summary>
    /// Handles headless CLI invocations (--list / --apply-profile "Name" / --emergency).
    /// Returns true if the app should exit immediately after processing (no UI shown).
    /// Output goes to the parent console when there is one (never a new console window) and
    /// always to the app log. Exit code: 0 = success, 1 = failure.
    /// While the Visor UI is running, only --list is allowed — display changes would fight
    /// the running session.
    /// </summary>
    private bool TryHandleCliArgs(string[] args)
    {
        // Per-action elevated helper: "Visor.exe --elevated <id>.req.json <id>.res.json" (started by
        // ElevationBroker via UAC). Handled before the log, mutex, config, hooks or any window exist and
        // touches none of them — validate the allowlisted ops, run them, write the result, exit.
        if (args.Length > 0 && args[0].Equals(Visor.Services.Elevation.ElevatedHelperHost.CliSwitch, StringComparison.OrdinalIgnoreCase))
        {
            int helperExit = 1;
            try
            {
                if (args.Length == 3)
                    helperExit = Visor.Services.Elevation.ElevatedHelperHost.Run(args[1], args[2]);
            }
            catch (Exception)
            {
                helperExit = 1;
            }
            Shutdown(helperExit);
            return true;
        }

        bool wantsList = false;
        bool wantsEmergency = false;
        string? applyProfileName = null;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("--list", StringComparison.OrdinalIgnoreCase))
                wantsList = true;
            else if (a.Equals("--emergency", StringComparison.OrdinalIgnoreCase))
                wantsEmergency = true;
            else if (a.Equals("--apply-profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                applyProfileName = args[++i];
        }

        if (!wantsList && !wantsEmergency && applyProfileName == null)
            return false;

        AppLog.StartupBanner();
        bool hasConsole = AttachConsole(AttachParentProcess);
        void Say(string line)
        {
            AppLog.Info("CLI: " + line);
            if (hasConsole) Console.WriteLine(line);
        }

        int exitCode = 0;
        Mutex? mutex = null;
        bool ownsMutex = false;
        ConfigService? config = null;
        DisplayEngine? engine = null;
        ProfileMonitor? monitor = null;
        try
        {
            mutex = new Mutex(true, SingleInstanceMutexName, out ownsMutex);
            bool guiRunning = !ownsMutex;

            config = new ConfigService();
            AppLog.Configure(config.LogPath);
            config.LoadOrCreate();

            if (wantsList && hasConsole)
            {
                Console.WriteLine("Visor - profiles:");
                if (config.Current.Profiles.Count == 0)
                    Console.WriteLine("  (none configured)");
                foreach (var p in config.Current.Profiles)
                    Console.WriteLine($"  [{(p.Enabled ? "enabled " : "disabled")}] {p.Name}  ({p.ProcessName})");
            }

            if ((wantsEmergency || applyProfileName != null) && guiRunning)
            {
                Say("Visor is running — use Emergency Restore / Profiles in its tray menu instead.");
                exitCode = 1;
            }
            else if (wantsEmergency)
            {
                // Restores %AppData%\Visor\active-session.json (display snapshot + session extras + FPS cap).
                engine = new DisplayEngine(config.ConfigDirectory);
                monitor = new ProfileMonitor(config, engine, new ProcessWatcher(), new CompanionService());
                if (monitor.HasLeftoverSession && monitor.RestoreLeftoverSession(out bool ok))
                {
                    Say(ok
                        ? "Emergency restore: game session snapshot restored."
                        : "Emergency restore: session snapshot restored with errors (see log).");
                    exitCode = ok ? 0 : 1;
                }
                else if (engine.InitializeBaseline(useSavedBaseline: true))
                {
                    Say("Emergency restore: saved display baseline re-applied.");
                }
                else
                {
                    Say("Emergency restore: nothing to restore (no session snapshot or saved baseline).");
                }
            }
            else if (applyProfileName != null)
            {
                var profile = config.Current.Profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, applyProfileName, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                {
                    Say($"Profile not found: {applyProfileName}");
                    exitCode = 1;
                }
                else
                {
                    engine = new DisplayEngine(config.ConfigDirectory);
                    engine.ApplyProfile(profile, config.Current.Defaults);
                    Say($"Applied profile: {profile.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            AppLog.Error(ex, "CLI");
            if (hasConsole) Console.WriteLine("CLI error: " + ex.Message);
        }
        finally
        {
            try { monitor?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "CLI monitor dispose"); }
            try { engine?.DisposeDriverColor(); } catch (Exception ex) { AppLog.Warn(ex, "CLI driver dispose"); }
            try { config?.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "CLI config dispose"); }
            if (ownsMutex)
            {
                try { mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            }
            mutex?.Dispose();
            AppLog.Shutdown();
        }

        Shutdown(exitCode);
        return true;
    }

    /// <summary>
    /// Named event the installer / uninstaller signals to ask a running Visor to exit cleanly
    /// (same path as tray Exit: restore the active session, mark clean exit, remove tray icon).
    /// </summary>
    private void StartExitRequestListener()
    {
        try
        {
            _exitRequestEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitRequestEventName);
            _exitRequestWait = ThreadPool.RegisterWaitForSingleObject(_exitRequestEvent, (_, timedOut) =>
            {
                if (timedOut) return;
                AppLog.Info("Exit requested by another process (installer).");
                Dispatcher.BeginInvoke(ExitFromExternalRequest);
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Exit request listener");
        }
    }

    /// <summary>Tray Exit without showing the window (autosave is flushed, session restored).</summary>
    private void ExitFromExternalRequest()
    {
        if (_cleanedUp) return;
        _exitRequested = true;
        try { _main?.PromptUnsavedBeforeExit(); }
        catch (Exception ex) { AppLog.Warn(ex, "Autosave flush on external exit"); }
        try { _overlay?.HideOverlay(); }
        catch (Exception ex) { AppLog.Warn(ex, "Overlay hide on exit"); }
        CleanupForExit(stopCompanions: true);
        Shutdown();
    }

    private void StopExitRequestListener()
    {
        try { _exitRequestWait?.Unregister(null); }
        catch (Exception ex) { AppLog.Warn(ex, "Exit request unregister"); }
        _exitRequestWait = null;
        _exitRequestEvent?.Dispose();
        _exitRequestEvent = null;
    }


    private void NotifyHotkeyFailures()
    {
        try
        {
            var fails = Services.Hotkeys.LastFailures;
            if (fails.Count == 0 || _main == null) return;
            var msg = fails.Count == 1
                ? Loc.Tf("toast.hotkey.busy", fails[0])
                : Loc.Tf("toast.hotkey.failed", fails.Count);
            _main.Dispatcher.BeginInvoke(() => _main.ShowToast(msg));
            AppLog.Warn("Hotkey registration failed: " + string.Join(", ", fails));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "NotifyHotkeyFailures");
        }
    }

    private void InitTray()
    {
        System.Drawing.Icon? trayIcon = null;
        try
        {
            using var stream = AssetLoader.OpenStream("app.ico");
            if (stream != null)
            {
                using var ms = new System.IO.MemoryStream();
                stream.CopyTo(ms);
                ms.Position = 0;
                using var tmp = new System.Drawing.Icon(ms);
                trayIcon = (System.Drawing.Icon)tmp.Clone();
            }
            else
            {
                AppLog.Warn("Tray icon: app.ico resource stream was null");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Tray icon load");
        }

        // Created once and reused by every menu rebuild (GDI handles; disposed on exit).
        _trayFont = new System.Drawing.Font("Segoe UI", 9.25f, System.Drawing.FontStyle.Regular);
        _trayFontBold = new System.Drawing.Font(_trayFont, System.Drawing.FontStyle.Bold);

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = Loc.T("app.name"),
            Visible = true,
            Icon = trayIcon ?? System.Drawing.SystemIcons.Application
        };
        _trayIcon = trayIcon;
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    private void DisposeTray()
    {
        if (_tray != null)
        {
            try
            {
                _tray.Visible = false;
                _tray.ContextMenuStrip?.Dispose();
                _tray.Dispose();
                _trayIcon?.Dispose();
                _trayIcon = null;
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Tray dispose");
            }
            _tray = null;
        }
        _trayFontBold?.Dispose();
        _trayFontBold = null;
        _trayFont?.Dispose();
        _trayFont = null;
    }

    public void RebuildTrayMenu()
    {
        if (_tray == null) return;

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new SlateMenuRenderer(),
            BackColor = System.Drawing.Color.FromArgb(0x16, 0x1B, 0x22),
            ForeColor = System.Drawing.Color.FromArgb(0xE8, 0xEE, 0xF4),
            Font = _trayFont,
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new System.Windows.Forms.Padding(0, 6, 0, 6),
            DropShadowEnabled = false,
            AutoSize = true
        };

        const int MaxTrayPresets = 12;
        var trayFore = System.Drawing.Color.FromArgb(0xE8, 0xEE, 0xF4);
        var trayBack = System.Drawing.Color.FromArgb(0x16, 0x1B, 0x22);

        System.Windows.Forms.ToolStripMenuItem MakeItem(string text, EventHandler onClick, int width = 220)
        {
            return new System.Windows.Forms.ToolStripMenuItem(text, null, onClick)
            {
                AutoSize = false,
                Width = width,
                Height = 32,
                Padding = new System.Windows.Forms.Padding(0),
                Margin = new System.Windows.Forms.Padding(0),
                ForeColor = trayFore,
                BackColor = trayBack,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
        }

        System.Windows.Forms.ToolStripMenuItem MakeSubmenu(string text)
        {
            var mi = MakeItem(text, (_, _) => { });
            mi.DropDown.Renderer = new SlateMenuRenderer();
            mi.DropDown.BackColor = trayBack;
            mi.DropDownItems.Clear();
            return mi;
        }

        menu.Items.Add(MakeItem(Loc.T("tray.open"), (_, _) => ShowMain()));
        menu.Items.Add(MakeItem(Loc.T("tray.overlay"), (_, _) => ToggleGameOverlay()));
        menu.Items.Add(MakeItem(
            Services.Monitor.IsPaused ? Loc.T("tray.resume") : Loc.T("tray.pause"),
            (_, _) =>
            {
                Services.Monitor.IsPaused = !Services.Monitor.IsPaused;
                RebuildTrayMenu();
            }));
        menu.Items.Add(MakeItem(Loc.T("tray.reset"), (_, _) =>
        {
            Services.Zoom.Off();
            Services.Monitor.EmergencyRestore();
        }));

        // Profiles submenu — activate any enabled profile on demand, regardless of running state.
        // Clicking the profile that is currently forced from here ends it (restores the PC).
        var profilesMenu = MakeSubmenu(Loc.T("tray.profiles"));
        var enabledProfiles = Services.Config.Current.Profiles.Where(p => p.Enabled).ToList();
        if (enabledProfiles.Count > 0)
        {
            bool forced = Services.Monitor.IsForcedActivation;
            foreach (var profile in enabledProfiles)
            {
                var pr = profile;
                bool isCurrent = Services.Monitor.CurrentProfile?.Id == pr.Id;
                bool stopsForced = isCurrent && forced;
                var item = MakeItem(stopsForced ? Loc.Tf("tray.profiles.stop", pr.Name) : pr.Name, (_, _) =>
                {
                    if (stopsForced)
                    {
                        Services.Monitor.EndForcedProfile();
                        _main?.ShowToast(Loc.Tf("toast.forced.ended", pr.Name));
                    }
                    else
                    {
                        Services.Monitor.ForceApplyProfile(pr);
                        _main?.ShowToast($"{Loc.T("btn.apply")}: {pr.Name}");
                    }
                }, width: 200);
                if (isCurrent)
                    item.Font = _trayFontBold;
                profilesMenu.DropDownItems.Add(item);
            }
        }
        else
        {
            var none = MakeItem(Loc.T("tray.profiles.none"), (_, _) => { }, width: 200);
            none.Enabled = false;
            profilesMenu.DropDownItems.Add(none);
        }
        menu.Items.Add(profilesMenu);

        // Hotkeys submenu — read-only reference; click copies the gesture to the clipboard.
        var hotkeysMenu = MakeSubmenu(Loc.T("tray.hotkeys"));
        var hk = Services.Config.Current.GlobalHotkeys ?? new Models.GlobalHotkeys();
        var globalBindings = new (string Label, string? Gesture)[]
        {
            (Loc.T("tray.hotkeys.toggleOverlay"), hk.ToggleOverlay),
            (Loc.T("tray.hotkeys.emergencyRestore"), hk.EmergencyRestore),
            (Loc.T("tray.hotkeys.nextPreset"), hk.NextPreset),
            (Loc.T("tray.hotkeys.previousPreset"), hk.PreviousPreset),
            (Loc.T("tray.hotkeys.compareAb"), hk.CompareAb),
            (Loc.T("hotkey.zoom"), hk.ToggleZoom),
        }.Where(b => !string.IsNullOrWhiteSpace(b.Gesture)).ToList();

        var activeForHotkeys = Services.Monitor.CurrentProfile;
        var presetBindings = (activeForHotkeys?.Presets ?? new List<Models.QuickPreset>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Hotkey))
            .Select(p => (Label: p.Name, Gesture: p.Hotkey))
            .ToList();

        if (globalBindings.Count == 0 && presetBindings.Count == 0)
        {
            var none = MakeItem(Loc.T("tray.hotkeys.none"), (_, _) => { }, width: 240);
            none.Enabled = false;
            hotkeysMenu.DropDownItems.Add(none);
        }
        else
        {
            foreach (var (label, gesture) in globalBindings)
                hotkeysMenu.DropDownItems.Add(MakeItem($"{label}: {gesture}", (_, _) => CopyHotkeyToClipboard(gesture!), width: 240));
            if (presetBindings.Count > 0)
            {
                hotkeysMenu.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator { AutoSize = false, Height = 7 });
                foreach (var (label, gesture) in presetBindings)
                    hotkeysMenu.DropDownItems.Add(MakeItem($"{label}: {gesture}", (_, _) => CopyHotkeyToClipboard(gesture!), width: 240));
            }
        }
        menu.Items.Add(hotkeysMenu);

        var active = Services.Monitor.CurrentProfile;
        var presets = active?.Presets?.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator { AutoSize = false, Height = 7 });
        if (active != null && presets != null && presets.Count > 0)
        {
            var header = MakeItem($"{Loc.T("tray.presets")}: {active.Name}", (_, _) => { });
            header.Enabled = false;
            menu.Items.Add(header);
            foreach (var preset in presets.Take(MaxTrayPresets))
            {
                var p = preset;
                menu.Items.Add(MakeItem("  " + p.Name, (_, _) =>
                {
                    Services.Monitor.ApplyPreset(p);
                    _main?.Dispatcher.Invoke(() => _main.ShowToast($"{Loc.T("btn.apply")}: {p.Name}"));
                }));
            }
            if (presets.Count > MaxTrayPresets)
            {
                var more = MakeItem($"  … +{presets.Count - MaxTrayPresets} more (Open app)", (_, _) => ShowMain());
                more.Enabled = false;
                menu.Items.Add(more);
            }
        }
        else
        {
            var none = MakeItem(Loc.T("tray.none"), (_, _) => { });
            none.Enabled = false;
            menu.Items.Add(none);
        }

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator { AutoSize = false, Height = 7 });
        menu.Items.Add(MakeItem(Loc.T("tray.exit"), (_, _) => ExitApp()));

        foreach (System.Windows.Forms.ToolStripItem it in menu.Items)
            if (it is System.Windows.Forms.ToolStripMenuItem mi && mi.DropDownItems.Count == 0) mi.Width = 220;

        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();

        UpdateTrayTooltip();
    }

    private static void CopyHotkeyToClipboard(string gesture)
    {
        try { System.Windows.Clipboard.SetText(gesture); }
        catch (Exception ex) { AppLog.Warn(ex, "Copy hotkey to clipboard"); }
    }

    /// <summary>Keeps the tray icon tooltip in sync with the active profile/preset.</summary>
    private void UpdateTrayTooltip()
    {
        if (_tray == null) return;
        try
        {
            var profile = Services.Monitor.CurrentProfile;
            string text;
            if (profile != null)
            {
                var presetId = Services.Monitor.ActivePresetId;
                var preset = presetId == null ? null : profile.Presets?.FirstOrDefault(p => p.Id == presetId);
                text = preset != null
                    ? $"Visor — {profile.Name} · {preset.Name}"
                    : $"Visor — {profile.Name}";
            }
            else
            {
                text = Loc.T("tray.idle");
            }
            // NotifyIcon.Text is limited to 63 chars on classic shells.
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Tray tooltip");
        }
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _mainHiddenForOverlay = false;
        _main.ShowInTaskbar = true; // cleared by tray start / overlay hide
        _main.ShowWithFade();
    }

    public void ShowGameOverlay(bool sync = true)
    {
        if (_overlay == null) return;
        HideMainForOverlay();
        if (sync) _overlay.SyncFromActiveContext();
        _overlay.ShowOverlay(expanded: Services.Config.Current.Ui?.OverlayExpanded ?? true);
        var ui = Services.Config.Current.Ui ?? new Models.UiPreferences();
        ui.OverlayVisible = true;
        Services.Config.Save(Services.Config.Current, raiseChanged: false);
    }

    public void ToggleGameOverlay()
    {
        if (_overlay == null) return;
        if (_overlay.IsOverlayVisible)
            HideGameOverlay();
        else
            ShowGameOverlay(sync: true);
    }

    public void HideGameOverlay()
    {
        _overlay?.HideOverlay();
        RestoreMainAfterOverlay();
    }

    /// <summary>Called by overlay when user closes it (×).</summary>
    public void NotifyOverlayHidden() => RestoreMainAfterOverlay();

    private void HideMainForOverlay()
    {
        if (_main == null) return;
        if (!_main.IsVisible && _main.WindowState == WindowState.Minimized) return;
        if (!_main.IsVisible) return;
        _mainHiddenForOverlay = true;
        try
        {
            _main.ShowInTaskbar = false;
            _main.Hide();
        }
        catch (Exception ex)
        {
            AppLog.Error("HideMainForOverlay: " + ex.Message);
            _mainHiddenForOverlay = false;
        }
    }

    private void RestoreMainAfterOverlay()
    {
        if (!_mainHiddenForOverlay || _main == null) return;
        _mainHiddenForOverlay = false;
        try
        {
            _main.ShowInTaskbar = true;
            _main.ShowWithFade();
        }
        catch (Exception ex)
        {
            AppLog.Error("RestoreMainAfterOverlay: " + ex.Message);
        }
    }

    public void ExitApp()
    {
        if (_main != null)
        {
            // Ensure window can show the dialog
            if (!_main.IsVisible)
                _main.ShowWithFade();
            if (!_main.PromptUnsavedBeforeExit())
                return;
        }

        _exitRequested = true;

        try { _overlay?.HideOverlay(); }
        catch (Exception ex) { AppLog.Warn(ex, "Overlay hide on exit"); }

        bool finished = false;
        void FinishExit()
        {
            if (finished) return;
            finished = true;
            CleanupForExit(stopCompanions: true);
            Shutdown();
        }

        if (_main != null && _main.IsVisible)
        {
            // Fade the chrome, not the Window: a window without AllowsTransparency ignores Opacity.
            // Hard cap so the exit never depends on an animation Completed callback.
            var guard = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
            guard.Tick += (_, _) => { guard.Stop(); FinishExit(); };
            guard.Start();
            UiMotion.PopOut(_main.ChromeRoot, () => { guard.Stop(); FinishExit(); }, ms: 150);
        }
        else
        {
            FinishExit();
        }
    }

    /// <summary>
    /// Synchronous restore of an active game session, service disposal (marks the clean exit),
    /// tray + mutex release. Idempotent — tray Exit, logoff and OnExit all end up here.
    /// Time box: tray exit ≤ ~9 s of waits (companions, driver queue) + 1 s NvAPI; logoff
    /// (<paramref name="stopCompanions"/> = false) restores synchronously and skips every optional wait.
    /// </summary>
    private void CleanupForExit(bool stopCompanions)
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        var started = DateTime.UtcNow;

        var services = Services;
        if (services != null)
        {
            try { services.Zoom.Off(); }
            catch (Exception ex) { AppLog.Warn(ex, "Zoom.Off on exit"); }

            try { services.Monitor.ShutdownRestore(stopCompanions); }
            catch (Exception ex) { AppLog.Error(ex, "Session restore on exit"); }

            try { services.Dispose(quick: !stopCompanions); }
            catch (Exception ex) { AppLog.Warn(ex, "Services.Dispose on exit"); }
        }

        DisposeTray();
        StopExitRequestListener();
        ReleaseMutex();
        AppLog.Info($"Exit cleanup done in {(DateTime.UtcNow - started).TotalMilliseconds:0} ms.");
        AppLog.Flush(); // logoff may terminate the process right after this returns
    }

    private void ReleaseMutex()
    {
        var m = _mutex;
        _mutex = null;
        if (m == null) return;
        try { m.ReleaseMutex(); }
        catch (ApplicationException) { /* not owned by this thread */ }
        m.Dispose();
    }

    public bool IsExitRequested => _exitRequested;
}
