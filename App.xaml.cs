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

        if (TryHandleCliArgs(e.Args))
            return;

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error(args.Exception, "UI exception");
            args.Handled = true;
            try { ThemedDialog.Show(null, "Something went wrong:\n" + args.Exception.Message); }
            catch { }
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
                try { _mutex.Dispose(); } catch { }
                _mutex = null;
                Shutdown();
                return;
            }

            Services = new AppServices();
            AppLog.StartupBanner();
            var ui = Services.Config.Current.Ui ?? new Models.UiPreferences();
            Loc.SetLocale(ui.Locale);
            ThemeService.Apply(ui);
            UiSound.ApplyFromConfig(ui);

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
                            }
                            else
                            {
                                Services.Monitor.HandleHotkey(action);
                                if ((action.StartsWith("preset:", StringComparison.OrdinalIgnoreCase))
                                    && !string.IsNullOrWhiteSpace(Services.Monitor.LastPresetHotkeyName))
                                    _main.ShowToast($"{Loc.T("toast.preset.hotkey")}: {Services.Monitor.LastPresetHotkeyName}");
                                else if (action is "nextPreset" or "previousPreset"
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

                RunConflictCheckFireAndForget();
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
                // Splash first, then soft welcome chime (after fade starts)
                UiSound.PlayWelcome(delayMs: 320);
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
    private static extern bool AllocConsole();

    /// <summary>
    /// Handles headless CLI invocations (--list / --apply-profile "Name" / --emergency).
    /// Returns true if the app should exit immediately after processing (no UI shown).
    /// </summary>
    private bool TryHandleCliArgs(string[] args)
    {
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

        try { AllocConsole(); } catch { }

        ConfigService? config = null;
        DisplayEngine? engine = null;
        try
        {
            config = new ConfigService();
            config.LoadOrCreate();

            if (wantsList)
            {
                Console.WriteLine("Display Profile Manager - profiles:");
                if (config.Current.Profiles.Count == 0)
                    Console.WriteLine("  (none configured)");
                foreach (var p in config.Current.Profiles)
                    Console.WriteLine($"  [{(p.Enabled ? "enabled " : "disabled")}] {p.Name}  ({p.ProcessName})");
            }

            if (wantsEmergency)
            {
                engine = new DisplayEngine();
                engine.CaptureFactoryGammaRamp(captureRamp: false);
                engine.RestoreFactory(config.Current.FactoryDefaults ?? config.Current.Defaults);
                Console.WriteLine("Emergency restore applied.");
            }
            else if (applyProfileName != null)
            {
                var profile = config.Current.Profiles.FirstOrDefault(p =>
                    string.Equals(p.Name, applyProfileName, StringComparison.OrdinalIgnoreCase));
                if (profile == null)
                {
                    Console.WriteLine($"Profile not found: {applyProfileName}");
                }
                else
                {
                    engine = new DisplayEngine();
                    engine.CaptureFactoryGammaRamp(captureRamp: true);
                    engine.ApplyProfile(profile, config.Current.Defaults);
                    Console.WriteLine($"Applied profile: {profile.Name}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("CLI error: " + ex.Message);
        }
        finally
        {
            try { engine?.DisposeDriverColor(); } catch { }
            try { config?.Dispose(); } catch { }
        }

        Shutdown();
        return true;
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

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = Loc.T("app.name"),
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
            Renderer = new SlateMenuRenderer(),
            BackColor = System.Drawing.Color.FromArgb(0x16, 0x1B, 0x22),
            ForeColor = System.Drawing.Color.FromArgb(0xE8, 0xEE, 0xF4),
            Font = new System.Drawing.Font("Segoe UI", 9.25f, System.Drawing.FontStyle.Regular),
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
        var profilesMenu = MakeSubmenu(Loc.T("tray.profiles"));
        var enabledProfiles = Services.Config.Current.Profiles.Where(p => p.Enabled).ToList();
        if (enabledProfiles.Count > 0)
        {
            foreach (var profile in enabledProfiles)
            {
                var pr = profile;
                var item = MakeItem(pr.Name, (_, _) =>
                {
                    Services.Monitor.ForceApplyProfile(pr);
                    _main?.Dispatcher.Invoke(() => _main.ShowToast($"{Loc.T("btn.apply")}: {pr.Name}"));
                }, width: 200);
                if (Services.Monitor.CurrentProfile?.Id == pr.Id)
                    item.Font = new System.Drawing.Font(menu.Font, System.Drawing.FontStyle.Bold);
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
        try { System.Windows.Clipboard.SetText(gesture); } catch { }
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
                    ? $"DPM — {profile.Name} · {preset.Name}"
                    : $"DPM — {profile.Name}";
            }
            else
            {
                text = Loc.T("tray.idle");
            }
            // NotifyIcon.Text is limited to 63 chars on classic shells.
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
        catch { }
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
            try { Services.Zoom.Off(); } catch (Exception ex) { AppLog.Warn(ex, "Zoom.Off on exit"); }
            try
            {
                // Snapshot / factory restore already set the intended gamma.
                Services.Monitor.EmergencyRestore();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "EmergencyRestore on exit");
            }
            try
            {
                Services.Dispose();
                SessionGuard.MarkCleanExit();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex, "Services.Dispose on exit");
            }

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
