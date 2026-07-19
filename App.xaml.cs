using System.Windows;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class App : System.Windows.Application
{
    public static AppServices Services { get; private set; } = null!;
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _main;
    private bool _exitRequested;
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
                Loc.SetLocale("en");
                try
                {
                    var existing = Services?.Config?.Current?.Ui?.Locale;
                    // Services not ready — default message bilingual-ish
                }
                catch { }
                ThemedDialog.Show(null, "Display Profile Manager is already running.\nDisplay Profile Manager уже запущен.");
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

            InitTray();
            RebuildTrayMenu();

            _main.SourceInitialized += (_, _) =>
            {
                try
                {
                    var helper = new System.Windows.Interop.WindowInteropHelper(_main);
                    Services.Hotkeys.Attach(helper.Handle);
                    Services.Hotkeys.RegisterFromConfig(Services.Config.Current, Services.Monitor.CurrentProfile);
                    Services.Hotkeys.HotkeyPressed += action =>
                        _main.Dispatcher.Invoke(() => Services.Monitor.HandleHotkey(action));
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
                    Services.Hotkeys.RegisterFromConfig(Services.Config.Current, Services.Monitor.CurrentProfile);
                    _main.ReloadFromConfig();
                    _main.ApplyLocalization();
                    RebuildTrayMenu();
                });

            Services.Monitor.ActiveProfileChanged += profile =>
                _main.Dispatcher.Invoke(() =>
                {
                    Services.Hotkeys.RegisterFromConfig(Services.Config.Current, profile);
                    _main.UpdateActiveHeader(profile);
                    RebuildTrayMenu();
                    if (profile != null && (Services.Config.Current.Ui?.NotifyOnGameStart ?? true))
                    {
                        var msg = $"{Loc.T("toast.game")}: {profile.Name}";
                        if (!string.IsNullOrWhiteSpace(profile.Resolution) && profile.ApplyResolution)
                            msg += $" · {profile.Resolution}";
                        _main.ShowToast(msg);
                        try
                        {
                            _tray?.ShowBalloonTip(2500, Loc.T("app.name"), msg,
                                System.Windows.Forms.ToolTipIcon.Info);
                        }
                        catch { }
                    }
                });

            Services.Monitor.StatusChanged += status =>
                _main.Dispatcher.Invoke(() =>
                {
                    _main.SetStatus(status);
                    if (_tray != null) _tray.Text = "DPM: " + status;
                });

            Services.Monitor.Start();

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
            Shutdown();
        }
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
        menu.Items.Add(MakeItem(
            Services.Monitor.IsPaused ? Loc.T("tray.resume") : Loc.T("tray.pause"),
            (_, _) =>
            {
                Services.Monitor.IsPaused = !Services.Monitor.IsPaused;
                RebuildTrayMenu();
            }));
        menu.Items.Add(MakeItem(Loc.T("tray.reset"), (_, _) => Services.Monitor.ResetDisplayNow()));

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
        _main.ShowWithFade();
    }

    public void ExitApp()
    {
        _exitRequested = true;

        void FinishExit()
        {
            try
            {
                Services.Monitor.ResetDisplayNow();
                Services.Dispose();
            }
            catch { }

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }

            _mutex?.ReleaseMutex();
            _mutex?.Dispose();
            Shutdown();
        }

        if (_main != null && _main.IsVisible)
            UiMotion.FadeTo(_main, 0, FinishExit, ms: 160);
        else
            FinishExit();
    }

    public bool IsExitRequested => _exitRequested;
}
