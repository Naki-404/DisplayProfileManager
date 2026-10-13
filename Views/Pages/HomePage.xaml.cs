using System.Windows;
using System.Windows.Controls;
using Visor.Controls;
using Visor.Models;
using Visor.Services;

namespace Visor;

/// <summary>
/// Home dashboard: welcome / health card, 2×2 stat cards (Session · Network · Games · Suggestions) and the
/// Optimize · Games · Settings row. Only data that already exists is shown; <c>HardwareCardsHost</c> stays
/// empty and collapsed until the hardware service lands.
/// </summary>
public partial class HomePage : ShellPageBase
{
    /// <summary>Set when a refresh was skipped because the page was hidden; honored by RefreshIfStale.</summary>
    private bool _stale = true;
    private bool _syncingToggles;
    private ConflictDetector.Result? _conflicts;
    private DateTime _conflictsUtc = DateTime.MinValue;
    private bool _narrow;
    private IDisposable? _sensorToken;
    private SensorSnapshot? _pendingSnapshot;
    private int _snapshotQueued;
    private int _healthQueued;

    public HomePage(IShellHost host) : base(host)
    {
        InitializeComponent();
        TxtUserName.Text = Environment.UserName;
        ShowHardwareMissing(UnavailableReason.NotSampledYet);
        // Window hidden to the tray (or shown again) flips IsVisible — the sampler must follow.
        IsVisibleChanged += (_, _) => SyncSensors();
        var services = App.Services;
        if (services != null)
        {
            services.HealthChanged += _ => QueueHealthRows();
            services.CrashSummaryChanged += QueueHealthRows;
        }
    }

    public override void OnDeactivated() => StopSensors();

    // ---------------------------------------------------------------- live sensors (only while Home is live)

    private void SyncSensors()
    {
        bool want = IsVisible && IsLive;
        if (want && _sensorToken == null) StartSensors();
        else if (!want && _sensorToken != null) StopSensors();
    }

    private void StartSensors()
    {
        var services = App.Services;
        if (services == null || _sensorToken != null) return;
        try
        {
            services.Sensors.SnapshotReady += OnSnapshotReady;
            _sensorToken = services.Sensors.Subscribe();
        }
        catch (ObjectDisposedException)
        {
            services.Sensors.SnapshotReady -= OnSnapshotReady;
        }
    }

    private void StopSensors()
    {
        var token = _sensorToken;
        if (token == null) return;
        _sensorToken = null;
        var services = App.Services;
        if (services != null) services.Sensors.SnapshotReady -= OnSnapshotReady;
        try { token.Dispose(); } catch (Exception ex) { AppLog.Warn(ex, "Sensor unsubscribe"); }
    }

    /// <summary>Sampler thread: keep the newest snapshot, queue at most one UI update.</summary>
    private void OnSnapshotReady(object? sender, SensorSnapshot snapshot)
    {
        Interlocked.Exchange(ref _pendingSnapshot, snapshot);
        if (Interlocked.Exchange(ref _snapshotQueued, 1) == 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _snapshotQueued, 0);
                var snap = Interlocked.Exchange(ref _pendingSnapshot, null);
                if (snap == null || _sensorToken == null) return;
                try { ApplySnapshot(snap); ApplyHealthRows(); }
                catch (Exception ex) { AppLog.Warn(ex, "Home sensor snapshot"); }
            });
    }

    private void QueueHealthRows()
    {
        if (Interlocked.Exchange(ref _healthQueued, 1) == 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _healthQueued, 0);
                if (!IsLive) return;
                try { ApplyHealthRows(); }
                catch (Exception ex) { AppLog.Warn(ex, "Home health rows"); }
            });
    }

    private void ShowHardwareMissing(UnavailableReason reason)
    {
        string tip = HardwareText.ReasonText(reason);
        foreach (var (value, bar, dot) in new[] { (TxtHwCpuValue, BarHwCpu, DotHwCpu), (TxtHwGpuValue, BarHwGpu, DotHwGpu), (TxtHwRamValue, BarHwRam, DotHwRam), (TxtHwStorageValue, BarHwStorage, DotHwStorage) })
        {
            value.Text = HardwareText.Missing;
            value.ToolTip = tip;
            bar.Value = 0;
            dot.Status = StatusKind.Unknown;
        }
        TxtHwCpuCaption.Text = "";
        TxtHwGpuCaption.Text = "";
        TxtHwRamCaption.Text = "";
        TxtHwStorageCaption.Text = "";
    }

    private void ApplySnapshot(SensorSnapshot s)
    {
        // CPU %
        if (s.CpuTotalPercent.HasValue)
        {
            double cpu = s.CpuTotalPercent.Value!.Value;
            TxtHwCpuValue.Text = HardwareText.FormatPercent(cpu) + " %";
            TxtHwCpuValue.ToolTip = null;
            BarHwCpu.Value = cpu;
            DotHwCpu.Status = StatusKind.Ok;
        }
        else
        {
            TxtHwCpuValue.Text = HardwareText.Missing;
            TxtHwCpuValue.ToolTip = HardwareText.ReasonText(s.CpuTotalPercent.EffectiveReason);
            BarHwCpu.Value = 0;
            DotHwCpu.Status = StatusKind.Unknown;
        }
        TxtHwCpuCaption.Text = HardwareInventoryService.Cached?.Cpu.Name ?? Loc.T("hw.util");

        // GPU °C + %
        var gpu = s.Gpus.FirstOrDefault(g => g.TemperatureC.HasValue || g.UtilizationPercent.HasValue) ?? s.Gpus.FirstOrDefault();
        if (gpu == null)
        {
            TxtHwGpuValue.Text = HardwareText.Missing;
            TxtHwGpuValue.ToolTip = Loc.T("hw.gpu.none");
            TxtHwGpuCaption.Text = "";
            BarHwGpu.Value = 0;
            DotHwGpu.Status = StatusKind.Unknown;
        }
        else
        {
            string temp = gpu.TemperatureC.HasValue ? gpu.TemperatureC.Value!.Value.ToString() : HardwareText.Missing;
            string util = gpu.UtilizationPercent.HasValue ? gpu.UtilizationPercent.Value!.Value.ToString() : HardwareText.Missing;
            TxtHwGpuValue.Text = Loc.Tf("home.hw.gpu.value", temp, util);
            TxtHwGpuValue.ToolTip = gpu.TemperatureC.HasValue && gpu.UtilizationPercent.HasValue ? null
                : HardwareText.ReasonText(gpu.TemperatureC.HasValue ? gpu.UtilizationPercent.EffectiveReason : gpu.TemperatureC.EffectiveReason);
            TxtHwGpuCaption.Text = gpu.Name;
            BarHwGpu.Value = gpu.UtilizationPercent.HasValue ? gpu.UtilizationPercent.Value!.Value : 0;
            DotHwGpu.Status = !gpu.TemperatureC.HasValue ? StatusKind.Unknown
                : gpu.TemperatureC.Value!.Value >= HealthEvaluator.GpuTempBadC ? StatusKind.Bad
                : gpu.TemperatureC.Value!.Value >= HealthEvaluator.GpuTempWarnC ? StatusKind.Warn : StatusKind.Ok;
        }

        // RAM %
        if (s.RamUsedPercent.HasValue && s.RamTotalBytes > 0)
        {
            double pct = s.RamUsedPercent.Value!.Value;
            TxtHwRamValue.Text = HardwareText.FormatPercent(pct) + " %";
            TxtHwRamValue.ToolTip = null;
            TxtHwRamCaption.Text = Loc.Tf("hw.ram.used", HardwareText.FormatGb(s.RamUsedBytes), HardwareText.FormatGb(s.RamTotalBytes));
            BarHwRam.Maximum = s.RamTotalBytes;
            BarHwRam.Value = s.RamUsedBytes;
            DotHwRam.Status = pct >= HealthEvaluator.RamUsedWarnPercent ? StatusKind.Warn : StatusKind.Ok;
        }
        else
        {
            TxtHwRamValue.Text = HardwareText.Missing;
            TxtHwRamValue.ToolTip = HardwareText.ReasonText(s.RamUsedPercent.EffectiveReason);
            TxtHwRamCaption.Text = "";
            BarHwRam.Value = 0;
            DotHwRam.Status = StatusKind.Unknown;
        }

        // Storage free % (system drive)
        var drive = HardwareText.FeaturedDrive(s.Drives);
        if (drive == null || drive.TotalBytes <= 0)
        {
            TxtHwStorageValue.Text = HardwareText.Missing;
            TxtHwStorageValue.ToolTip = HardwareText.ReasonText(UnavailableReason.NoDevice);
            TxtHwStorageCaption.Text = "";
            BarHwStorage.Value = 0;
            DotHwStorage.Status = StatusKind.Unknown;
        }
        else
        {
            double free = drive.FreePercent;
            TxtHwStorageValue.Text = Loc.Tf("home.hw.storage.value", drive.Name, free.ToString("0.#"));
            TxtHwStorageValue.ToolTip = null;
            TxtHwStorageCaption.Text = Loc.Tf("system.ram.value", SystemInfoService.FormatBytes(drive.FreeBytes), SystemInfoService.FormatBytes(drive.TotalBytes));
            BarHwStorage.Maximum = drive.TotalBytes;
            BarHwStorage.Value = drive.FreeBytes;
            DotHwStorage.Status = free < HealthEvaluator.DiskFreeBadPercent ? StatusKind.Bad
                : free < HealthEvaluator.DiskFreeWarnPercent ? StatusKind.Warn : StatusKind.Ok;
        }

        // Network card: ↑↓ bytes/s when the adapter counters are available.
        var net = s.Network;
        if (net.ReceiveBytesPerSec.HasValue && net.SendBytesPerSec.HasValue)
        {
            TxtHomeNetRate.Text = Loc.Tf("home.network.rate", HardwareText.FormatRate(net.ReceiveBytesPerSec.Value!.Value), HardwareText.FormatRate(net.SendBytesPerSec.Value!.Value));
            TxtHomeNetRate.Visibility = Visibility.Visible;
        }
        else TxtHomeNetRate.Visibility = Visibility.Collapsed;
    }

    /// <summary>Welcome-card rows: Temperatures / Health / Stability / Drivers from HealthReport, snapshot, crash summary, inventory.</summary>
    private void ApplyHealthRows()
    {
        var services = App.Services;
        if (services == null) return;
        var snap = _sensorToken != null ? services.Sensors.Latest : null;

        // Temperatures (GPU only — CPU temperature needs a kernel driver and is never estimated)
        bool? hot = HardwareText.TemperaturesHot(snap);
        TxtHealthTemps.Text = hot == null ? Loc.T("home.health.na") : Loc.T(hot.Value ? "home.health.temps.hot" : "home.health.temps.optimal");
        TxtHealthTemps.ToolTip = hot != null ? null
            : snap == null ? Loc.T(HealthReasons.NotSampledYet)
            : snap.Gpus.Count == 0 ? Loc.T("hw.gpu.none")
            : HardwareText.ReasonText(snap.Gpus[0].TemperatureC.EffectiveReason);
        DotTemps.Status = hot == null ? StatusKind.Unknown : hot.Value ? StatusKind.Warn : StatusKind.Ok;

        // Health: last evaluation
        var health = services.Health;
        if (health == null)
        {
            TxtHealthScan.Text = Loc.T("home.health.scan.none");
            TxtHealthScan.ToolTip = null;
            DotScan.Status = StatusKind.Unknown;
        }
        else
        {
            TxtHealthScan.Text = Loc.Tf("home.health.scan.value", HardwareText.LocalTime(health.EvaluatedUtc));
            var top = health.Findings.FirstOrDefault();
            TxtHealthScan.ToolTip = top == null ? null : HardwareText.FindingText(top);
            DotScan.Status = health.Overall switch
            {
                HealthStatus.Bad => StatusKind.Bad,
                HealthStatus.Warn => StatusKind.Warn,
                HealthStatus.Unknown => StatusKind.Unknown,
                _ => StatusKind.Ok
            };
        }

        // Stability: crash events in the last 30 days
        var crashes = services.CrashSummary;
        if (crashes == null || crashes.Reason != UnavailableReason.None)
        {
            TxtHealthStability.Text = Loc.T("home.health.na");
            TxtHealthStability.ToolTip = crashes == null ? Loc.T("system.loading") : HardwareText.ReasonText(crashes.Reason);
            DotStability.Status = StatusKind.Unknown;
        }
        else if (crashes.Total == 0)
        {
            TxtHealthStability.Text = Loc.T("home.health.stability.clean");
            TxtHealthStability.ToolTip = null;
            DotStability.Status = StatusKind.Ok;
        }
        else
        {
            bool serious = crashes.Count(CrashEventKind.UnexpectedShutdown) > 0 || crashes.Count(CrashEventKind.WheaFatal) > 0
                           || crashes.Count(CrashEventKind.DisplayTdr) > 0 || crashes.Count(CrashEventKind.WheaCorrected) >= HealthEvaluator.WheaCorrectedWarnCount;
            TxtHealthStability.Text = Loc.Plural("unit.events", crashes.Total);
            TxtHealthStability.ToolTip = Loc.T("hw.stability.shutdowns") + ": " + crashes.Count(CrashEventKind.UnexpectedShutdown)
                + " · " + Loc.T("hw.stability.wheaFatal") + ": " + crashes.Count(CrashEventKind.WheaFatal)
                + " · " + Loc.T("hw.stability.tdr") + ": " + crashes.Count(CrashEventKind.DisplayTdr);
            DotStability.Status = serious ? StatusKind.Warn : StatusKind.Ok;
        }

        // Drivers: GPU driver date from the inventory
        bool? old = HardwareText.DriversOld(HardwareInventoryService.Cached, DateTime.UtcNow);
        TxtHealthDrivers.Text = old == null ? Loc.T("home.health.na") : Loc.T(old.Value ? "home.health.drivers.old" : "home.health.drivers.ok");
        TxtHealthDrivers.ToolTip = old == null ? Loc.T("system.loading") : null;
        DotDrivers.Status = old == null ? StatusKind.Unknown : old.Value ? StatusKind.Warn : StatusKind.Ok;
    }

    public override ShellSection Section => ShellSection.Home;
    public override string TitleKey => "nav.home";

    /// <summary>Host panel for the hardware cards a later stage adds (collapsed, no fake numbers).</summary>
    public System.Windows.Controls.Panel HardwareHost => HardwareCardsHost;

    public override void ApplyLocalization()
    {
        LblWelcome.Text = Loc.T("home.welcome");
        LblHealthDisplay.Text = Loc.T("home.health.display");
        LblHealthMonitoring.Text = Loc.T("home.health.monitoring.lbl");
        LblHealthProfiles.Text = Loc.T("home.health.profiles");
        LblHealthActivity.Text = Loc.T("home.health.activity");
        LblHealthConflicts.Text = Loc.T("home.health.conflicts");
        TxtHealthCrash.Text = Loc.T("home.health.crashRestored");
        DotDisplay.Label = LblHealthDisplay.Text;
        DotMonitoring.Label = LblHealthMonitoring.Text;
        DotProfiles.Label = LblHealthProfiles.Text;
        DotActivity.Label = LblHealthActivity.Text;
        DotConflicts.Label = LblHealthConflicts.Text;
        DotCrash.Label = TxtHealthCrash.Text;
        LblHealthTemps.Text = Loc.T("home.health.temps");
        LblHealthScan.Text = Loc.T("home.health.scan");
        LblHealthStability.Text = Loc.T("home.health.stability");
        LblHealthDrivers.Text = Loc.T("home.health.drivers");
        DotTemps.Label = LblHealthTemps.Text;
        DotScan.Label = LblHealthScan.Text;
        DotStability.Label = LblHealthStability.Text;
        DotDrivers.Label = LblHealthDrivers.Text;
        LblHwCpu.Text = Loc.T("hw.cpu");
        LblHwGpu.Text = Loc.T("hw.gpu");
        LblHwRam.Text = Loc.T("system.ram");
        LblHwStorage.Text = Loc.T("home.hw.storage");
        DotHwCpu.Label = LblHwCpu.Text;
        DotHwGpu.Label = LblHwGpu.Text;
        DotHwRam.Label = LblHwRam.Text;
        DotHwStorage.Label = LblHwStorage.Text;
        BarHwCpu.Label = LblHwCpu.Text;
        BarHwGpu.Label = LblHwGpu.Text;
        BarHwRam.Label = LblHwRam.Text;
        BarHwStorage.Label = LblHwStorage.Text;
        BtnHomeMyPc.Content = Loc.T("home.myPc");

        LblHomeSession.Text = Loc.T("home.session");
        DotSession.Label = LblHomeSession.Text;
        BtnHomeEmergency.Content = Loc.T("btn.emergency");
        BtnHomeEmergency.ToolTip = Loc.T("btn.emergency.tip");
        LblHomeNetwork.Text = Loc.T("nav.network");
        DotNetwork.Label = LblHomeNetwork.Text;
        BtnHomeNetwork.Content = Loc.T("home.network.open");
        LblHomeGames.Text = Loc.T("home.library");
        BtnHomeGamesStat.Content = Loc.T("home.games.open");
        LblHomeSuggestions.Text = Loc.T("rec.header");
        DotSuggestions.Label = LblHomeSuggestions.Text;
        BtnHomeSuggestions.Content = Loc.T("home.suggestions.open");

        LblHomeOptimize.Text = Loc.T("nav.optimize");
        BtnHomeOptimize.Content = Loc.T("home.optimize.open");
        DotGameMode.Label = Loc.T("system.gameMode");
        DotPower.Label = Loc.T("system.power");
        LblHomeQuick.Text = Loc.T("home.quick");
        BtnHomeOpenGames.Content = Loc.T("home.games.open");
        BtnHomeScan.Content = Loc.T("btn.scan");
        BtnHomeAdd.Content = Loc.T("btn.add");
        TxtHomeQuickEmpty.Text = Loc.T("home.quick.empty");
        LblHomeSettings.Text = Loc.T("nav.settings");
        BtnHomeSettings.Content = Loc.T("home.settings.open");
        TogAutostart.Content = Loc.T("home.settings.autostart");
        TogTray.Content = Loc.T("home.settings.tray");
        TogSounds.Content = Loc.T("home.settings.sounds");
        TogTooltips.Content = Loc.T("shell.tooltipMode");
        TogTooltips.ToolTip = Loc.T("shell.tooltipMode.tip");
        Refresh();
    }

    public override void OnActivated()
    {
        Refresh();
        SyncSensors();
    }

    public override void RefreshIfStale()
    {
        if (_stale) Refresh();
    }

    /// <summary>Rebuilds every card from live services. Skipped (and marked stale) while the page is hidden.</summary>
    public void Refresh()
    {
        if (TxtHomeSessionStatus == null) return;
        if (!IsLive)
        {
            _stale = true;
            return;
        }
        _stale = false;

        var monitor = App.Services.Monitor;
        var cfg = App.Services.Config.Current;
        var profiles = Host.Profiles;
        bool paused = monitor.IsPaused;
        var active = monitor.CurrentProfile;

        // -- Session card
        if (paused)
        {
            TxtHomeSessionStatus.Text = Loc.T("home.session.paused");
            TxtHomeSessionDetail.Text = Loc.T("home.session.paused.detail");
            DotSession.Status = StatusKind.Warn;
        }
        else if (active != null)
        {
            TxtHomeSessionStatus.Text = Loc.Tf("home.session.active", active.Name);
            var bits = new List<string>();
            if (active.ApplyResolution && !string.IsNullOrWhiteSpace(active.Resolution))
                bits.Add(ColorUiHelper.FormatResolution(active.Resolution));
            if (monitor.HasActivePreset)
                bits.Add(Loc.T("home.session.presetOn"));
            TxtHomeSessionDetail.Text = bits.Count > 0 ? string.Join(" · ", bits) : (active.ProcessName ?? "");
            DotSession.Status = StatusKind.Ok;
        }
        else
        {
            TxtHomeSessionStatus.Text = Loc.T("home.session.idle");
            TxtHomeSessionDetail.Text = Loc.T("home.session.idle.detail");
            DotSession.Status = StatusKind.Unknown;
        }
        BtnHomePause.Content = Loc.T(paused ? "nav.resume" : "nav.pause");
        BtnHomePause.ToolTip = Loc.T(paused ? "nav.resume.tip" : "nav.pause.tip");

        // -- Health rows
        var mode = DisplayEngine.GetCurrentMode(cfg.Ui?.PreferredDisplayDevice);
        TxtHealthDisplay.Text = mode.RefreshRate > 0
            ? Loc.Tf("home.display.mode", mode.Resolution, mode.RefreshRate)
            : mode.Resolution;
        DotDisplay.Status = string.IsNullOrWhiteSpace(mode.Resolution) ? StatusKind.Unknown : StatusKind.Ok;

        TxtHealthMonitoring.Text = Loc.T(paused ? "home.health.paused.short" : "home.health.on");
        DotMonitoring.Status = paused ? StatusKind.Warn : StatusKind.Ok;

        int total = profiles.Count;
        int enabled = profiles.Count(p => p.Enabled);
        int presets = profiles.Sum(p => p.Presets?.Count ?? 0);
        TxtHealthProfiles.Text = Loc.Tf("home.health.profiles.value", total, enabled);
        DotProfiles.Status = total == 0 ? StatusKind.Unknown : enabled > 0 ? StatusKind.Ok : StatusKind.Warn;

        var last = App.Services.History.Recent(1).FirstOrDefault();
        if (last == null)
        {
            TxtHealthActivity.Text = Loc.T("home.health.activity.none");
            TxtHealthActivity.ToolTip = null;
            DotActivity.Status = StatusKind.Unknown;
        }
        else
        {
            TxtHealthActivity.Text = HistoryText.Body(last);
            TxtHealthActivity.ToolTip = HistoryText.Format(last);
            DotActivity.Status = last.Success ? StatusKind.Ok : StatusKind.Warn;
        }

        if (_conflicts == null || (DateTime.UtcNow - _conflictsUtc).TotalSeconds > 5)
        {
            _conflicts = ConflictDetector.Scan();
            _conflictsUtc = DateTime.UtcNow;
        }
        if (_conflicts.HasAny)
        {
            var parts = new List<string>();
            if (_conflicts.ConflictingApps.Count > 0)
                parts.Add(string.Join(", ", _conflicts.ConflictingApps));
            if (_conflicts.NightLightOn)
                parts.Add(Loc.T("home.health.conflict.nightLight"));
            TxtHealthConflicts.Text = parts.Count > 0 ? string.Join(" · ", parts) : Loc.T("home.health.conflict");
            TxtHealthConflicts.ToolTip = TxtHealthConflicts.Text;
            DotConflicts.Status = StatusKind.Warn;
        }
        else
        {
            TxtHealthConflicts.Text = Loc.T("home.health.conflicts.none");
            TxtHealthConflicts.ToolTip = null;
            DotConflicts.Status = StatusKind.Ok;
        }
        bool crash = App.Services.CrashRestored;
        RowCrash.Height = crash ? new GridLength(24) : new GridLength(0);
        DotCrash.Visibility = crash ? Visibility.Visible : Visibility.Collapsed;
        TxtHealthCrash.Visibility = crash ? Visibility.Visible : Visibility.Collapsed;
        ApplyHealthRows();
        var latest = _sensorToken != null ? App.Services.Sensors.Latest : null;
        if (latest != null) ApplySnapshot(latest);

        // -- Network card (cached capture, 2 s TTL inside the service)
        var net = NetworkInfoService.CapturePrimary();
        if (net == null)
        {
            TxtHomeNetAdapter.Text = Loc.T("home.network.none");
            TxtHomeNetDetail.Text = "";
            DotNetwork.Status = StatusKind.Bad;
        }
        else
        {
            TxtHomeNetAdapter.Text = Loc.Tf("net.adapter.line", net.Name, NetworkPage.LocalizeNetType(net.TypeCode));
            var bits = new List<string>();
            var speed = NetworkInfoService.LinkSpeedParts(net.SpeedBps);
            if (speed is { } sp)
                bits.Add(sp.Gigabits ? Loc.Tf("net.speed.gbps", sp.Value.ToString("0.#")) : Loc.Tf("net.speed.mbps", sp.Value.ToString("0.#")));
            bool hasGw = !string.IsNullOrEmpty(net.Gateway);
            bits.Add(hasGw ? Loc.Tf("net.gateway", net.Gateway!) : Loc.T("net.gateway.missing"));
            TxtHomeNetDetail.Text = string.Join(" · ", bits);
            DotNetwork.Status = net.Status == System.Net.NetworkInformation.OperationalStatus.Up
                ? (hasGw ? StatusKind.Ok : StatusKind.Warn)
                : StatusKind.Bad;
        }

        // -- Games card
        TxtHomeGamesCount.Text = Loc.Plural("unit.games", total);
        TxtHomeGamesDetail.Text = Loc.Tf("home.library.counts", Loc.Plural("unit.games", total), enabled, Loc.Plural("unit.presets", presets));

        // -- Suggestions card
        var recs = OptimizePage.Evaluate(profiles);
        TxtHomeSuggestionsCount.Text = recs.Count == 0 ? Loc.T("rec.empty") : Loc.Plural("unit.suggestions", recs.Count);
        TxtHomeSuggestionsTop.Text = recs.Count == 0 ? "" : Loc.T(recs[0].TitleKey);
        TxtHomeSuggestionsTop.ToolTip = recs.Count == 0 ? null : OptimizePage.FormatRecommendationBody(recs[0]);
        DotSuggestions.Status = recs.Count == 0 ? StatusKind.Ok
            : recs.Any(r => r.Severity == RecommendationSeverity.Warning) ? StatusKind.Warn : StatusKind.Unknown;

        // -- Optimize card
        var gm = GameModeSetting.Read();
        TxtHomeGameMode.Text = Loc.T("system.gameMode") + ": " + (gm == null
            ? Loc.T("system.unavailable")
            : Loc.T(gm.Value ? "system.gameMode.on.short" : "system.gameMode.off"));
        DotGameMode.Status = StatusDot.FromBool(gm);
        var plan = PowerPlanSafeAction.ReadAliasOrGuid();
        TxtHomePower.Text = Loc.T("system.power") + ": " + (plan == null ? Loc.T("system.unavailable") : ComboUi.PowerPlanStateLabel(plan));
        DotPower.Status = plan == null ? StatusKind.Unknown
            : plan == "highPerformance" || plan == "ultimate" ? StatusKind.Ok : StatusKind.Warn;

        RefreshQuickGames();

        // -- Settings card (real toggles)
        _syncingToggles = true;
        try
        {
            TogAutostart.IsChecked = cfg.StartWithWindows;
            TogTray.IsChecked = cfg.Ui?.MinimizeToTrayOnClose != false;
            TogSounds.IsChecked = cfg.Ui?.UiSoundsEnabled != false;
            TogTooltips.IsChecked = Host.TooltipMode;
        }
        finally
        {
            _syncingToggles = false;
        }
    }

    /// <summary>Real recent activity only — never padded with arbitrary library entries.</summary>
    private void RefreshQuickGames()
    {
        HomeQuickList.Children.Clear();
        var recentIds = App.Services.History.Recent(40)
            .Where(e => !string.IsNullOrEmpty(e.ProfileId)
                        && (e.Kind is "profileApplied" or "presetApplied" or "sessionRestored"))
            .Select(e => e.ProfileId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();

        var games = new List<GameProfile>();
        foreach (var id in recentIds)
        {
            var match = Host.Profiles.FirstOrDefault(p => p.Id == id);
            if (match != null) games.Add(match);
        }

        TxtHomeQuickEmpty.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in games)
        {
            var profile = g;
            var btn = new System.Windows.Controls.Button
            {
                Content = new TextBlock { Text = profile.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 140 },
                Style = (Style)FindResource("ChipButton"),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = profile.ProcessName
            };
            System.Windows.Automation.AutomationProperties.SetName(btn, profile.Name);
            btn.Click += (_, _) => Host.OpenGame(profile);
            HomeQuickList.Children.Add(btn);
        }
    }

    /// <summary>Below ~720 DIP the stat grid drops under the welcome card; below ~600 the bottom row stacks.</summary>
    private void HomeRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool narrow = e.NewSize.Width < 720;
        if (narrow != _narrow || e.PreviousSize.Width <= 0)
        {
            _narrow = narrow;
            if (narrow)
            {
                ColWelcome.Width = new GridLength(0);
                ColGap.Width = new GridLength(0);
                Grid.SetColumn(WelcomeCard, 2);
                Grid.SetRow(WelcomeCard, 0);
                Grid.SetRow(StatGrid, 1);
            }
            else
            {
                ColWelcome.Width = new GridLength(300);
                ColGap.Width = new GridLength(12);
                Grid.SetColumn(WelcomeCard, 0);
                Grid.SetRow(WelcomeCard, 0);
                Grid.SetRow(StatGrid, 0);
            }
        }
        BottomRow.Columns = e.NewSize.Width < 600 ? 1 : 3;
        HwStrip.Columns = e.NewSize.Width < 720 ? 2 : 4;
    }

    // -- Actions --------------------------------------------------------------------------------------------

    private void OpenMyPc_Click(object sender, RoutedEventArgs e) => Host.Navigate(ShellSection.MyPc);
    private void OpenNetwork_Click(object sender, RoutedEventArgs e) => Host.Navigate(ShellSection.Network);
    private void OpenGames_Click(object sender, RoutedEventArgs e) => Host.Navigate(ShellSection.Games);
    private void OpenOptimize_Click(object sender, RoutedEventArgs e) => Host.Navigate(ShellSection.Optimize);
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => Host.Navigate(ShellSection.Settings);
    private void Scan_Click(object sender, RoutedEventArgs e) => Host.ScanGames();
    private void Add_Click(object sender, RoutedEventArgs e) => Host.AddGame();
    private void Pause_Click(object sender, RoutedEventArgs e) => Host.TogglePauseMonitoring();

    /// <summary>Home sits next to Pause — a misclick must not reset the display without asking.</summary>
    private void Emergency_Click(object sender, RoutedEventArgs e)
    {
        if (!ThemedDialog.Confirm(Host.Window, Loc.T("confirm.emergency"), Loc.T("btn.emergency"), Loc.T("btn.emergency"), danger: true))
            return;
        Host.EmergencyRestore();
    }

    /// <summary>Each switch writes straight to the config (same fields as the Preferences dialog).</summary>
    private void SettingToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles || sender is not System.Windows.Controls.Primitives.ToggleButton tog) return;
        bool on = tog.IsChecked == true;
        var cfg = App.Services.Config.Current;
        cfg.Ui ??= new UiPreferences();

        if (ReferenceEquals(tog, TogTooltips))
        {
            Host.TooltipMode = on; // the shell persists it
            return;
        }
        if (ReferenceEquals(tog, TogAutostart))
        {
            cfg.StartWithWindows = on;
            var exe = Environment.ProcessPath ?? "";
            if (!string.IsNullOrWhiteSpace(exe) && !AutostartService.SetEnabled(on, exe))
                ThemedDialog.Show(Host.Window, Loc.T("autostart.failed"), Loc.T("settings.behavior"));
        }
        else if (ReferenceEquals(tog, TogTray))
            cfg.Ui.MinimizeToTrayOnClose = on;
        else if (ReferenceEquals(tog, TogSounds))
        {
            cfg.Ui.UiSoundsEnabled = on;
            UiSound.ApplyFromConfig(cfg.Ui);
        }
        App.Services.Config.Save(cfg);
        if (System.Windows.Application.Current is App app)
            app.RebuildTrayMenu();
    }
}
