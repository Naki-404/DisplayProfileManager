using System.Windows;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; } = null!;
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _main;
    private GameOverlayWindow? _overlay;
    private bool _exitRequested;
    private bool _mainHiddenForOverlay;
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("UI exception: " + args.Exception.Message);
            args.Handled = true;
            try { ThemedDialog.Show(null, "Something went wrong:\n" + args.Exception.Message); }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Error("Unhandled: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Error("Task exception: " + args.Exception);
            args.SetObserved();
        };

        try
        {
            bool created;
            _mutex = new Mutex(true, @"Local\DisplayProfileManager.SingleInstance", out created);
            if (!created)
            {
                bool silent = e.Args.Any(a =>
                    a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("/minimized", StringComparison.OrdinalIgnoreCase) ||
                    a.Equals("--silent", StringComparison.OrdinalIgnoreCase));
                if (!silent)
                {
                    Loc.SetLocale("en");
                    ThemedDialog.Show(null, Loc.T("alreadyRunning"));
                }
                Shutdown();
                return;
            }

            Services = new AppServices();
            var ui = Services.Config.Current.Ui ?? new Models.UiPreferences();
            Loc.SetLocale(ui.Locale);
            ThemeService.Apply(ui);
            AppLog.Info("Application starting.");

            bool startMinimized = e.Args.Any(a =>
                a.Equals("--minimized", StringComparison.OrdinalIgnoreCase) ||
                a.Equals("/minimized", StringComparison.OrdinalIgnoreCase));

            // First-run wizard (new config only)
            if (!ui.SetupCompleted && !startMinimized)
            {
                try
                {
                    var wiz = new SetupWizardWindow();
                    wiz.ShowDialog();
                    ui = Services.Config.Current.Ui ?? ui;
                    Loc.SetLocale(ui.Locale);
                    ThemeService.Apply(ui);
                }
                catch (Exception ex)
                {
                    AppLog.Error("Setup wizard failed: " + ex.Message);
                    ui.SetupCompleted = true;
                    Services.Config.Current.Ui = ui;
                    Services.Config.Save(Services.Config.Current);
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
                    Services.Hotkeys.HotkeyPressed += action =>
                        _main.Dispatcher.Invoke(() =>
                        {
                            Services.Monitor.HandleHotkey(action);
                            if ((action.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
                                && !string.IsNullOrWhiteSpace(Services.Monitor.LastPresetHotkeyName))
                                _main.ShowToast($"{Loc.T("toast.preset.hotkey")}: {Services.Monitor.LastPresetHotkeyName}");
                            else if (action == "emergencyRestore")
                                _main.ShowToast(Loc.T("toast.emergency"));
                        });
                }
                catch (Exception ex)
                {
                    AppLog.Error("Hotkey attach failed: " + ex);
                }
            };

            Services.Config.ConfigChanged += () =>
                _main.Dispatcher.Invoke(() =>
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
                });

            Services.Monitor.ActiveProfileChanged += profile =>
                _main.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
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
                                if (pr != null) msg += $"\nColor: {pr.Name}";
                            }
                            var detail = Services.Monitor.LastApplyToastDetail;
                            if (!string.IsNullOrWhiteSpace(detail))
                                msg += "\n" + detail;
                            else if (Services.Monitor.SnapshotActive)
                                msg += "\n" + Loc.T("toast.snapshot");
                            _main.ShowToast(msg);
                            try
                            {
                                _tray?.ShowBalloonTip(2800, Loc.T("app.name"), msg.Replace("\n", " · "),
                                    System.Windows.Forms.ToolTipIcon.Info);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("ActiveProfileChanged UI: " + ex.Message);
                    }
                });

            Services.Monitor.StatusChanged += status =>
                _main.Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        _main.SetStatus(status);
                        if (_tray != null) _tray.Text = "DPM: " + status;
                    }
                    catch { }
                });

            // Start monitoring after the window exists — avoids applying while UI is half-built.
            _main.Loaded += (_, _) =>
            {
                try { Services.Monitor.Start(); }
                catch (Exception ex) { AppLog.Error("Monitor start failed: " + ex.Message); }

                try
                {
                    var gpu = GpuVendorDetect.DriverLabel;
                    var msg = Loc.Tf("toast.health", gpu);
                    if (Services.CrashRestored)
                        msg += " · " + Loc.T("toast.crash.restored");
                    _main.ShowToast(msg);
                    AppLog.Info("Startup health: " + msg);

                    if (!string.IsNullOrWhiteSpace(Services.Config.LastRecoveryMessage))
                    {
                        ThemedDialog.Show(_main, Services.Config.LastRecoveryMessage);
                        _main.ShowToast(Loc.T("toast.config.recovered"));
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("Startup health toast: " + ex.Message);
                }
            };

            try
            {
                if (Services.Config.Current.StartWithWindows)
                {
                    var exe = Environment.ProcessPath
                              ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                              ?? "";
                    if (!string.IsNullOrWhiteSpace(exe))
                        AutostartService.SetEnabled(true, exe);
                }
            }
            catch (Exception ex)
            {
                AppLog.Error("Autostart setup failed: " + ex.Message);
            }

            if (startMinimized || Services.Config.Current.StartMinimized)
            {
                _main.ShowInTaskbar = false;
                _main.WindowState = WindowState.Minimized;
                _main.Show();
                _main.Hide();
            }
            else
            {
                try
                {
                    var splash = new BootSplashWindow();
                    splash.ShowDialog();
                }
                catch (Exception ex)
                {
                    AppLog.Error("Boot splash failed: " + ex.Message);
                }

                _main.ShowInTaskbar = true;
                _main.WindowState = WindowState.Normal;
                _main.ShowWithFade();
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup failed: " + ex);
            try { ThemedDialog.Show(null, "Startup failed:\n" + ex.Message); } catch { }
            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
            Shutdown();
        }
    }

    private void NotifyHotkeyFailures()
    {
        try
        {
            var fails = Services.Hotkeys.LastFailures;
            if (fails.Count == 0 || _main == null) return;
            var msg = fails.Count == 1
                ? $"Hotkey busy: {fails[0]}"
                : $"{fails.Count} hotkeys failed (in use?)";
            _main.Dispatcher.BeginInvoke(() => _main.ShowToast(msg));
        }
        catch { }
    }

    private void InitTray()
    {
        System.Drawing.Icon? trayIcon = null;
        try
        {
            using var stream = AssetLoader.OpenStream("app.ico");
            if (stream != null)
                trayIcon = new System.Drawing.Icon(stream);
        }
        catch { }

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Display Profile Manager",
            Visible = true,
            Icon = trayIcon ?? System.Drawing.SystemIcons.Application
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    public void RebuildTrayMenu()
    {
        if (_tray == null) return;

        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            Renderer = new DarkPinkMenuRenderer(),
            BackColor = System.Drawing.Color.FromArgb(0x1A, 0x14, 0x18),
            ForeColor = System.Drawing.Color.FromArgb(0xF3, 0xE6, 0xEC),
            Font = new System.Drawing.Font("Segoe UI", 9.25f, System.Drawing.FontStyle.Regular),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            Padding = new System.Windows.Forms.Padding(0, 6, 0, 6),
            DropShadowEnabled = false,
            AutoSize = true
        };

        System.Windows.Forms.ToolStripMenuItem MakeItem(string text, EventHandler onClick, int width = 200)
        {
            return new System.Windows.Forms.ToolStripMenuItem(text, null, onClick)
            {
                AutoSize = false,
                Width = width,
                Height = 32,
                Padding = new System.Windows.Forms.Padding(0),
                Margin = new System.Windows.Forms.Padding(0),
                ForeColor = System.Drawing.Color.FromArgb(0xF3, 0xE6, 0xEC),
                BackColor = System.Drawing.Color.FromArgb(0x1A, 0x14, 0x18),
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
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
        menu.Items.Add(MakeItem(Loc.T("tray.reset"), (_, _) => Services.Monitor.EmergencyRestore()));

        var active = Services.Monitor.CurrentProfile;
        var presets = active?.Presets?.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Take(5).ToList();
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator { AutoSize = false, Height = 7 });
        if (active != null && presets != null && presets.Count > 0)
        {
            var header = MakeItem($"{Loc.T("tray.presets")}: {active.Name}", (_, _) => { });
            header.Enabled = false;
            menu.Items.Add(header);
            foreach (var preset in presets)
            {
                var p = preset;
                menu.Items.Add(MakeItem("  " + p.Name, (_, _) =>
                {
                    Services.Monitor.ApplyPreset(p);
                    _main?.Dispatcher.Invoke(() => _main.ShowToast($"{Loc.T("btn.apply")}: {p.Name}"));
                }));
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
            if (it is System.Windows.Forms.ToolStripMenuItem mi) mi.Width = 200;

        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = menu;
        old?.Dispose();
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _mainHiddenForOverlay = false;
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

        try { _overlay?.HideOverlay(); } catch { }

        void FinishExit()
        {
            try
            {
                // Snapshot / factory restore already set the intended gamma.
                Services.Monitor.EmergencyRestore();
            }
            catch { }
            try
            {
                Services.Dispose();
                SessionGuard.MarkCleanExit();
            }
            catch { }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

            try { _mutex?.ReleaseMutex(); } catch { }
            try { _mutex?.Dispose(); } catch { }
            _mutex = null;
            Shutdown();
        }

        if (_main != null && _main.IsVisible)
            UiMotion.FadeTo(_main, 0, FinishExit, ms: 160);
        else
            FinishExit();
    }

    public bool IsExitRequested => _exitRequested;
}
