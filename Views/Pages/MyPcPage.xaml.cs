using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Visor.Controls;
using Visor.Services;

namespace Visor;

/// <summary>
/// My PC section: inventory snapshot (CPU / RAM / GPU / Windows / storage), live hardware cards and a read-only
/// process sample. Hardware cards read <see cref="HardwareInventoryService"/> (once, off-thread),
/// <see cref="SensorService"/> (1 Hz, subscribed only while this page is live and visible),
/// <see cref="AppServices.Health"/> and <see cref="AppServices.CrashSummary"/>. A value that cannot be read shows
/// "—" with the reason as a tooltip — never an estimate.
/// </summary>
public partial class MyPcPage : ShellPageBase
{
    /// <summary>True while the PC snapshot is being captured off-thread.</summary>
    private bool _busy;
    private bool _processBusy;
    private bool _inventoryBusy;
    /// <summary>A refresh was requested while the page was hidden — honored on the next selection.</summary>
    private bool _stale = true;
    private bool _hwTwoColumns = true;
    private bool _hwLayoutDone;

    private IDisposable? _sensorToken;
    private SensorSnapshot? _pendingSnapshot;
    private int _snapshotQueued;
    private HealthReport? _pendingHealth;
    private int _healthQueued;
    private int _crashQueued;
    private string? _findingsSignature;
    private HardwareInventory? _inventory;
    private readonly Dictionary<string, VolumeRow> _volumeRows = new(StringComparer.OrdinalIgnoreCase);
    private Border[] _hwCards = Array.Empty<Border>();

    private sealed class ProcessRowVm
    {
        public required string Title { get; init; }
        public required string CpuText { get; init; }
        public required string RamText { get; init; }
    }

    private sealed class VolumeRow
    {
        public required StackPanel Root { get; init; }
        public required TextBlock Line { get; init; }
        public required TextBlock Io { get; init; }
        public required ThinBar Bar { get; init; }
    }

    public MyPcPage(IShellHost host) : base(host)
    {
        InitializeComponent();
        // Nested MaxHeight list inside the page ScrollViewer — don't let it trap the wheel.
        NestedScroll.ForwardWheelAtEdge(ProcessList);
        _hwCards = [CardCpu, CardGpu, CardRam, CardStorage, CardBoard, CardOs, CardStability];
        LayoutHardwareCards();

        GaugeCpu.Caption = "CPU";
        GaugeGpu.Caption = "GPU";
        GaugeRam.Caption = "RAM";
        SparkCpu.Label = "CPU";
        SparkGpu.Label = "GPU";
        ShowMissing(GaugeCpu, UnavailableReason.NotSampledYet);
        ShowMissing(GaugeGpu, UnavailableReason.NotSampledYet);
        ShowMissing(GaugeRam, UnavailableReason.NotSampledYet);

        // Window hidden to the tray (or shown again) flips IsVisible on every page — the sampler must follow.
        IsVisibleChanged += (_, _) => SyncSensors();
        var services = App.Services;
        if (services != null)
        {
            services.HealthChanged += OnHealthChanged;
            services.CrashSummaryChanged += OnCrashSummaryChanged;
        }
    }

    public override ShellSection Section => ShellSection.MyPc;
    public override string TitleKey => "nav.mypc";

    /// <summary>Host panel of the hardware cards (health summary + responsive card grid).</summary>
    public System.Windows.Controls.Panel HardwareHost => HardwareCardsHost;

    public override void ApplyLocalization()
    {
        LblSystemOverview.Text = Loc.T("system.overview");
        LblSystemCpu.Text = Loc.T("system.cpu");
        LblSystemRam.Text = Loc.T("system.ram");
        LblSystemGpu.Text = Loc.T("system.gpu");
        LblSystemOs.Text = Loc.T("system.os");
        LblSystemDisks.Text = Loc.T("system.disks");
        TxtSystemDisksEmpty.Text = Loc.T("system.disks.empty");
        LblSystemProcesses.Text = Loc.T("system.processes");
        TxtProcessHint.Text = Loc.T("system.processes.hint");
        TxtProcessEmpty.Text = Loc.T("system.processes.empty");
        BtnSystemRefresh.Content = Loc.T("system.refresh");
        BtnProcessRefresh.Content = Loc.T("system.refresh");
        BarSystemRam.Label = Loc.T("system.ram");

        LblHealthTitle.Text = Loc.T("hw.health.title");
        DotHealthOverall.Label = LblHealthTitle.Text;
        BtnHwRefresh.Content = Loc.T("system.refresh");
        TxtHwLiveHint.Text = Loc.T("hw.live.hint");
        LblCpuTitle.Text = Loc.T("hw.cpu");
        TxtCpuTemp.Text = Loc.T("hw.cpu.temp");
        TxtCpuTemp.ToolTip = Loc.T(HealthReasons.NoDriver);
        BarCpu.Label = Loc.T("hw.util");
        LblGpuTitle.Text = Loc.T("hw.gpu");
        BarGpu.Label = Loc.T("hw.util");
        DotGpuThrottle.Label = LblGpuTitle.Text;
        LblRamTitle.Text = Loc.T("system.ram");
        BarRam.Label = LblRamTitle.Text;
        LblStorageTitle.Text = Loc.T("system.disks");
        LblStorageDisks.Text = Loc.T("hw.disk.physical");
        LblStorageVolumes.Text = Loc.T("hw.disk.volumes");
        TxtStorageDisksEmpty.Text = Loc.T("hw.disk.none");
        LblBoardTitle.Text = Loc.T("hw.board");
        LblOsTitle.Text = Loc.T("system.os");
        LblStabilityTitle.Text = Loc.T("hw.stability");
        DotStability.Label = LblStabilityTitle.Text;
        BtnEventViewer.Content = Loc.T("hw.stability.openEventViewer");

        // Re-render cached data in the new language (cheap; no I/O).
        _findingsSignature = null;
        _volumeRows.Clear();
        StorageVolumes.Children.Clear();
        ApplyInventory(_inventory ?? HardwareInventoryService.Cached);
        ApplyCrashSummary();
        var health = App.Services?.Health;
        if (health != null) ApplyHealth(health);
        else ShowHealthWaiting();
        var snap = _sensorToken != null ? App.Services?.Sensors.Latest : null;
        if (snap != null) ApplySnapshot(snap);

        _stale = true;
        RefreshIfStale();
    }

    public override void OnActivated()
    {
        RefreshSnapshot(includeProcesses: false);
        SyncSensors();
        LoadInventory(force: false);
        ApplyCrashSummary();
        var health = App.Services?.Health;
        if (health != null) ApplyHealth(health);
    }

    public override void OnDeactivated() => StopSensors();

    public override void RefreshIfStale()
    {
        if (_stale) RefreshSnapshot(includeProcesses: false);
    }

    // ---------------------------------------------------------------- sensors (subscribe only while live)

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
            // A gap in the sparklines marks the pause instead of joining old and new samples.
            SparkCpu.Push(null);
            SparkGpu.Push(null);
            if (services.Health == null) ShowHealthWaiting();
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

    /// <summary>Sampler thread: keep only the newest snapshot and queue one UI update at a time.</summary>
    private void OnSnapshotReady(object? sender, SensorSnapshot snapshot)
    {
        Interlocked.Exchange(ref _pendingSnapshot, snapshot);
        if (Interlocked.Exchange(ref _snapshotQueued, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyPendingSnapshot);
    }

    private void ApplyPendingSnapshot()
    {
        Interlocked.Exchange(ref _snapshotQueued, 0);
        var snap = Interlocked.Exchange(ref _pendingSnapshot, null);
        if (snap == null || _sensorToken == null) return;
        try { ApplySnapshot(snap); }
        catch (Exception ex) { AppLog.Warn(ex, "Apply sensor snapshot"); }
    }

    private void OnHealthChanged(HealthReport report)
    {
        Interlocked.Exchange(ref _pendingHealth, report);
        if (Interlocked.Exchange(ref _healthQueued, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ApplyPendingHealth);
    }

    private void ApplyPendingHealth()
    {
        Interlocked.Exchange(ref _healthQueued, 0);
        var report = Interlocked.Exchange(ref _pendingHealth, null);
        if (report == null) return;
        try { ApplyHealth(report); }
        catch (Exception ex) { AppLog.Warn(ex, "Apply health"); }
    }

    private void OnCrashSummaryChanged()
    {
        if (Interlocked.Exchange(ref _crashQueued, 1) == 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _crashQueued, 0);
                try { ApplyCrashSummary(); }
                catch (Exception ex) { AppLog.Warn(ex, "Apply crash summary"); }
            });
    }

    // ---------------------------------------------------------------- live values

    private void ApplySnapshot(SensorSnapshot s)
    {
        // CPU
        if (s.CpuTotalPercent.HasValue)
        {
            double cpu = s.CpuTotalPercent.Value!.Value;
            ShowValue(GaugeCpu, cpu, HardwareText.FormatPercent(cpu) + "%");
            BarCpu.Value = cpu;
            TxtCpuUtil.Text = Loc.Tf("hw.util.value", HardwareText.FormatPercent(cpu));
            TxtCpuUtil.ToolTip = null;
            SparkCpu.Push(cpu);
        }
        else
        {
            var reason = s.CpuTotalPercent.EffectiveReason;
            ShowMissing(GaugeCpu, reason);
            BarCpu.Value = 0;
            TxtCpuUtil.Text = Loc.T("hw.util") + " " + HardwareText.Missing;
            TxtCpuUtil.ToolTip = HardwareText.ReasonText(reason);
            SparkCpu.Push(null);
        }

        // GPU — the first adapter with any live sensor; others are listed below.
        var gpu = s.Gpus.FirstOrDefault(g => g.TemperatureC.HasValue || g.UtilizationPercent.HasValue) ?? s.Gpus.FirstOrDefault();
        ApplyGpuSensors(gpu, s.Gpus);

        // RAM
        if (s.RamUsedPercent.HasValue && s.RamTotalBytes > 0)
        {
            double pct = s.RamUsedPercent.Value!.Value;
            ShowValue(GaugeRam, pct, HardwareText.FormatPercent(pct) + "%");
            BarRam.Maximum = s.RamTotalBytes;
            BarRam.Value = s.RamUsedBytes;
            TxtRamUsed.Text = Loc.Tf("hw.ram.used", HardwareText.FormatGb(s.RamUsedBytes), HardwareText.FormatGb(s.RamTotalBytes));
            TxtRamUsed.ToolTip = null;
        }
        else
        {
            var reason = s.RamUsedPercent.EffectiveReason;
            ShowMissing(GaugeRam, reason);
            BarRam.Value = 0;
            TxtRamUsed.Text = HardwareText.Missing;
            TxtRamUsed.ToolTip = HardwareText.ReasonText(reason);
        }

        ApplyVolumes(s.Drives);
        TxtOsUptime.Text = Loc.Tf("hw.os.uptime", HardwareText.FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
    }

    private void ApplyGpuSensors(GpuSensorReading? gpu, IReadOnlyList<GpuSensorReading> all)
    {
        if (gpu == null)
        {
            ShowMissing(GaugeGpu, UnavailableReason.NoDevice, Loc.T("hw.gpu.none"));
            BarGpu.Value = 0;
            TxtGpuUtil.Text = Loc.T("hw.util") + " " + HardwareText.Missing;
            TxtGpuUtil.ToolTip = Loc.T("hw.gpu.none");
            TxtGpuClocks.Text = Loc.Tf("hw.gpu.clocks", HardwareText.Missing, HardwareText.Missing);
            TxtGpuVram.Text = Loc.Tf("hw.gpu.vramUsed", HardwareText.Missing);
            TxtGpuPower.Text = HardwareText.Missing;
            DotGpuThrottle.Status = StatusKind.Unknown;
            TxtGpuThrottle.Text = Loc.T("hw.gpu.throttle.na");
            TxtGpuThrottle.ToolTip = Loc.T("hw.gpu.none");
            TxtGpuOthers.Visibility = Visibility.Collapsed;
            SparkGpu.Push(null);
            return;
        }

        if (gpu.TemperatureC.HasValue)
        {
            int t = gpu.TemperatureC.Value!.Value;
            ShowValue(GaugeGpu, t, t + " °C");
        }
        else ShowMissing(GaugeGpu, gpu.TemperatureC.EffectiveReason);

        if (gpu.UtilizationPercent.HasValue)
        {
            int u = gpu.UtilizationPercent.Value!.Value;
            BarGpu.Value = u;
            TxtGpuUtil.Text = Loc.Tf("hw.util.value", u);
            TxtGpuUtil.ToolTip = null;
            SparkGpu.Push(u);
        }
        else
        {
            BarGpu.Value = 0;
            TxtGpuUtil.Text = Loc.T("hw.util") + " " + HardwareText.Missing;
            TxtGpuUtil.ToolTip = HardwareText.ReasonText(gpu.UtilizationPercent.EffectiveReason);
            SparkGpu.Push(null);
        }

        TxtGpuClocks.Text = Loc.Tf("hw.gpu.clocks",
            gpu.CoreClockMhz.HasValue ? gpu.CoreClockMhz.Value!.Value.ToString() : HardwareText.Missing,
            gpu.MemoryClockMhz.HasValue ? gpu.MemoryClockMhz.Value!.Value.ToString() : HardwareText.Missing);
        TxtGpuClocks.ToolTip = gpu.CoreClockMhz.HasValue ? null : HardwareText.ReasonText(gpu.CoreClockMhz.EffectiveReason);

        if (gpu.VramUsedBytes.HasValue)
        {
            TxtGpuVram.Text = Loc.Tf("hw.gpu.vramUsed", SystemInfoService.FormatBytes(gpu.VramUsedBytes.Value!.Value));
            TxtGpuVram.ToolTip = null;
        }
        else
        {
            TxtGpuVram.Text = Loc.Tf("hw.gpu.vramUsed", HardwareText.Missing);
            TxtGpuVram.ToolTip = HardwareText.ReasonText(gpu.VramUsedBytes.EffectiveReason);
        }

        if (gpu.PowerPercent.HasValue)
        {
            TxtGpuPower.Text = Loc.Tf("hw.gpu.power", HardwareText.FormatPercent(gpu.PowerPercent.Value!.Value));
            TxtGpuPower.ToolTip = null;
        }
        else
        {
            TxtGpuPower.Text = Loc.Tf("hw.gpu.power", HardwareText.Missing);
            TxtGpuPower.ToolTip = HardwareText.ReasonText(gpu.PowerPercent.EffectiveReason);
        }

        if (gpu.Throttle.HasValue)
        {
            var flags = gpu.Throttle.Value!.Value;
            var warn = flags & HardwareText.WarningFlags;
            if (flags == GpuThrottleFlags.None)
            {
                DotGpuThrottle.Status = StatusKind.Ok;
                TxtGpuThrottle.Text = Loc.T("hw.gpu.throttle.none");
            }
            else if (warn != GpuThrottleFlags.None)
            {
                DotGpuThrottle.Status = StatusKind.Warn;
                TxtGpuThrottle.Text = Loc.Tf("hw.gpu.throttle.warn", HardwareText.ThrottleList(flags));
            }
            else
            {
                // Voltage ceiling at max boost / idle: the normal state, informational only.
                DotGpuThrottle.Status = StatusKind.Ok;
                TxtGpuThrottle.Text = Loc.Tf("hw.gpu.throttle.info", HardwareText.ThrottleList(flags));
            }
            TxtGpuThrottle.ToolTip = null;
        }
        else
        {
            DotGpuThrottle.Status = StatusKind.Unknown;
            TxtGpuThrottle.Text = Loc.T("hw.gpu.throttle.na");
            TxtGpuThrottle.ToolTip = HardwareText.ReasonText(gpu.Throttle.EffectiveReason);
        }

        var others = all.Where(g => !ReferenceEquals(g, gpu)).Select(g => g.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        if (others.Count > 0)
        {
            TxtGpuOthers.Text = Loc.Tf("hw.gpu.others", string.Join(", ", others));
            TxtGpuOthers.Visibility = Visibility.Visible;
        }
        else TxtGpuOthers.Visibility = Visibility.Collapsed;
    }

    private void ApplyVolumes(IReadOnlyList<DriveSensorReading> drives)
    {
        // Rebuild rows only when the set of volumes changes; otherwise update text in place (1 Hz).
        bool sameSet = drives.Count == _volumeRows.Count && drives.All(d => _volumeRows.ContainsKey(d.Name));
        if (!sameSet)
        {
            _volumeRows.Clear();
            StorageVolumes.Children.Clear();
            foreach (var d in drives)
            {
                var root = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
                var line = new TextBlock { Style = (Style)FindResource("HwLine"), Margin = new Thickness(0, 0, 0, 4) };
                var io = new TextBlock { Style = (Style)FindResource("HwMuted"), Margin = new Thickness(0, 4, 0, 0) };
                var bar = new ThinBar { Label = d.Name };
                root.Children.Add(line);
                root.Children.Add(bar);
                root.Children.Add(io);
                StorageVolumes.Children.Add(root);
                _volumeRows[d.Name] = new VolumeRow { Root = root, Line = line, Io = io, Bar = bar };
            }
        }
        foreach (var d in drives)
        {
            var row = _volumeRows[d.Name];
            if (d.TotalBytes > 0)
            {
                row.Line.Text = d.Name + " — " + Loc.Tf("hw.volume.free",
                    SystemInfoService.FormatBytes(d.FreeBytes), SystemInfoService.FormatBytes(d.TotalBytes),
                    d.FreePercent.ToString("0.#"));
                row.Bar.Maximum = d.TotalBytes;
                row.Bar.Value = d.FreeBytes;
            }
            else
            {
                row.Line.Text = d.Name + " — " + HardwareText.Missing;
                row.Bar.Value = 0;
            }
            if (d.ReadBytesPerSec.HasValue && d.WriteBytesPerSec.HasValue)
            {
                row.Io.Text = Loc.Tf("hw.volume.io", HardwareText.FormatRate(d.ReadBytesPerSec.Value!.Value), HardwareText.FormatRate(d.WriteBytesPerSec.Value!.Value));
                row.Io.ToolTip = null;
            }
            else
            {
                row.Io.Text = Loc.Tf("hw.volume.io", HardwareText.Missing, HardwareText.Missing);
                row.Io.ToolTip = HardwareText.ReasonText(d.ReadBytesPerSec.EffectiveReason);
            }
        }
    }

    private static void ShowValue(RingGauge gauge, double value, string text)
    {
        gauge.Value = value;
        gauge.Text = text;
        gauge.ToolTip = null;
    }

    private static void ShowMissing(RingGauge gauge, UnavailableReason reason, string? tooltip = null)
    {
        gauge.Value = 0;
        gauge.Text = HardwareText.Missing;
        gauge.ToolTip = tooltip ?? HardwareText.ReasonText(reason);
    }

    // ---------------------------------------------------------------- health summary

    private void ShowHealthWaiting()
    {
        DotHealthOverall.Status = StatusKind.Unknown;
        TxtHealthChecked.Text = "";
        if (_findingsSignature == "waiting") return;
        _findingsSignature = "waiting";
        HealthFindings.Children.Clear();
        HealthFindings.Children.Add(new TextBlock { Text = Loc.T("hw.health.waiting"), Style = (Style)FindResource("HwMuted") });
        TxtHealthSkipped.Visibility = Visibility.Collapsed;
    }

    private void ApplyHealth(HealthReport r)
    {
        DotHealthOverall.Status = r.Overall switch
        {
            HealthStatus.Bad => StatusKind.Bad,
            HealthStatus.Warn => StatusKind.Warn,
            HealthStatus.Unknown => StatusKind.Unknown,
            _ => StatusKind.Ok
        };
        TxtHealthChecked.Text = Loc.Tf("hw.health.checked", HardwareText.LocalTime(r.EvaluatedUtc));

        string signature = string.Join("|", r.Findings.Select(f => $"{f.Status}:{f.ReasonKey}:{f.Subject}:{f.Detail}"))
                           + "#" + string.Join("|", r.Skipped.Select(f => f.Component + ":" + f.ReasonKey));
        if (signature == _findingsSignature) return;
        _findingsSignature = signature;

        HealthFindings.Children.Clear();
        foreach (var f in r.Findings)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var dot = new StatusDot
            {
                Status = f.Status switch
                {
                    HealthStatus.Bad => StatusKind.Bad,
                    HealthStatus.Warn => StatusKind.Warn,
                    HealthStatus.Unknown => StatusKind.Unknown,
                    _ => StatusKind.Ok
                },
                Margin = new Thickness(0, 0, 8, 0)
            };
            DockPanel.SetDock(dot, Dock.Left);
            var text = new TextBlock { Text = HardwareText.FindingText(f), Style = (Style)FindResource("HwLine"), Margin = new Thickness(0), TextWrapping = TextWrapping.Wrap };
            dot.Label = text.Text;
            row.Children.Add(dot);
            row.Children.Add(text);
            HealthFindings.Children.Add(row);
        }

        if (r.Skipped.Count == 0)
            TxtHealthSkipped.Visibility = Visibility.Collapsed;
        else
        {
            var parts = r.Skipped
                .Select(f => (f.Component == "cpu.temperature" ? Loc.T("hw.health.cpuTemp") : f.Subject ?? f.Component) + " — " + Loc.T(f.ReasonKey))
                .Distinct()
                .ToList();
            TxtHealthSkipped.Text = Loc.Tf("hw.health.skipped", string.Join("; ", parts));
            TxtHealthSkipped.Visibility = Visibility.Visible;
        }
    }

    // ---------------------------------------------------------------- inventory (static facts)

    private async void LoadInventory(bool force)
    {
        if (_inventoryBusy) return;
        var cached = HardwareInventoryService.Cached;
        if (!force && cached != null)
        {
            if (!ReferenceEquals(cached, _inventory)) ApplyInventory(cached);
            return;
        }
        _inventoryBusy = true;
        BtnHwRefresh.IsEnabled = false;
        if (_inventory == null)
        {
            string loading = Loc.T("system.loading");
            TxtCpuName.Text = loading;
            TxtGpuName.Text = loading;
            TxtRamTotal.Text = loading;
            TxtBoardName.Text = loading;
            TxtOsLabel.Text = loading;
        }
        try
        {
            var inv = await (force ? HardwareInventoryService.RefreshAsync() : HardwareInventoryService.GetAsync()).ConfigureAwait(true);
            ApplyInventory(inv);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Hardware inventory");
            ApplyInventory(HardwareInventoryService.Cached);
        }
        finally
        {
            _inventoryBusy = false;
            BtnHwRefresh.IsEnabled = true;
        }
    }

    private void ApplyInventory(HardwareInventory? inv)
    {
        _inventory = inv;
        string unavailable = Loc.T("system.unavailable");
        if (inv == null)
        {
            TxtCpuName.Text = unavailable;
            TxtCpuDetail.Text = "";
            TxtGpuName.Text = unavailable;
            TxtGpuDetail.Text = "";
            TxtRamTotal.Text = unavailable;
            TxtRamDetail.Text = "";
            TxtBoardName.Text = unavailable;
            TxtBiosDetail.Text = "";
            TxtMonitors.Text = "";
            TxtOsLabel.Text = unavailable;
            StorageDisks.Children.Clear();
            TxtStorageDisksEmpty.Visibility = Visibility.Visible;
            return;
        }

        // CPU
        TxtCpuName.Text = inv.Cpu.Name ?? unavailable;
        var cpuBits = new List<string>(2)
        {
            inv.Cpu.PhysicalCores is { } cores
                ? Loc.Tf("hw.cpu.cores", cores, inv.Cpu.LogicalCores)
                : Loc.Tf("hw.cpu.threads", inv.Cpu.LogicalCores)
        };
        if (inv.Cpu.BaseMhz is { } mhz)
            cpuBits.Add(Loc.Tf("hw.cpu.base", (mhz / 1000.0).ToString("0.00")));
        TxtCpuDetail.Text = string.Join(" · ", cpuBits);

        // GPU (static part): prefer the adapter with live sensors (NVIDIA), else the first one.
        var gpu = inv.Gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia) ?? inv.Gpus.FirstOrDefault();
        if (gpu == null)
        {
            TxtGpuName.Text = unavailable;
            TxtGpuName.ToolTip = inv.GpusReason == UnavailableReason.None ? null : HardwareText.ReasonText(inv.GpusReason);
            TxtGpuDetail.Text = "";
        }
        else
        {
            TxtGpuName.Text = gpu.Name;
            TxtGpuName.ToolTip = null;
            var bits = new List<string>(2);
            if (gpu.VramBytes is { } vram)
                bits.Add(Loc.Tf(gpu.VramMayBeCapped ? "hw.gpu.vram.capped" : "hw.gpu.vram", SystemInfoService.FormatBytes(vram)));
            if (gpu.DriverVersion != null)
                bits.Add(gpu.DriverDate is { } dd
                    ? Loc.Tf("hw.gpu.driver.dated", gpu.DriverVersion, HardwareText.LocalDate(dd))
                    : Loc.Tf("hw.gpu.driver", gpu.DriverVersion));
            TxtGpuDetail.Text = string.Join(" · ", bits);
        }

        // RAM
        TxtRamTotal.Text = inv.Ram.TotalBytes > 0 ? HardwareText.FormatGb(inv.Ram.TotalBytes) : unavailable;
        var ramBits = new List<string>(3);
        if (inv.Ram.Type != null) ramBits.Add(inv.Ram.Type);
        if (inv.Ram.SpeedMts is { } mts) ramBits.Add(Loc.Tf("hw.ram.speed", mts));
        if (inv.Ram.Modules.Count > 0) ramBits.Add(Loc.Tf("hw.ram.modules", inv.Ram.Modules.Count));
        TxtRamDetail.Text = ramBits.Count > 0 ? string.Join(" · ", ramBits) : HardwareText.Missing;
        TxtRamDetail.ToolTip = inv.Ram.ModulesReason == UnavailableReason.None ? null : HardwareText.ReasonText(inv.Ram.ModulesReason);

        // Physical disks
        StorageDisks.Children.Clear();
        if (inv.Storage.Count == 0)
        {
            TxtStorageDisksEmpty.Visibility = Visibility.Visible;
            TxtStorageDisksEmpty.ToolTip = inv.StorageReason == UnavailableReason.None ? null : HardwareText.ReasonText(inv.StorageReason);
        }
        else
        {
            TxtStorageDisksEmpty.Visibility = Visibility.Collapsed;
            foreach (var disk in inv.Storage)
            {
                var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
                var dot = new StatusDot
                {
                    Status = disk.Health switch
                    {
                        StorageHealth.Healthy => StatusKind.Ok,
                        StorageHealth.Warning => StatusKind.Bad,
                        StorageHealth.Unhealthy => StatusKind.Bad,
                        _ => StatusKind.Unknown
                    },
                    Label = disk.Model,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                dot.Margin = new Thickness(0, 6, 8, 0);
                DockPanel.SetDock(dot, Dock.Left);
                var text = new StackPanel();
                text.Children.Add(new TextBlock { Text = disk.Model, Style = (Style)FindResource("HwLine"), Margin = new Thickness(0), TextTrimming = TextTrimming.CharacterEllipsis });
                var caption = new TextBlock { Style = (Style)FindResource("HwMuted") };
                string line = HardwareText.DiskLine(disk);
                string smart = Loc.T(HardwareText.SmartKey(disk.Health));
                caption.Text = line.Length > 0 ? line + " · " + smart : smart;
                caption.ToolTip = disk.Health == null && disk.HealthReason != UnavailableReason.None ? HardwareText.ReasonText(disk.HealthReason) : null;
                text.Children.Add(caption);
                row.Children.Add(dot);
                row.Children.Add(text);
                StorageDisks.Children.Add(row);
            }
        }

        // Board / BIOS / monitors
        var board = inv.Board;
        if (board == null)
        {
            TxtBoardName.Text = unavailable;
            TxtBoardName.ToolTip = inv.BoardReason == UnavailableReason.None ? null : HardwareText.ReasonText(inv.BoardReason);
            TxtBiosDetail.Text = "";
        }
        else
        {
            string name = string.Join(" ", new[] { board.Manufacturer, board.Product }.Where(s => !string.IsNullOrWhiteSpace(s)));
            TxtBoardName.Text = name.Length > 0 ? name : unavailable;
            TxtBoardName.ToolTip = null;
            string vendor = board.BiosVendor ?? "", version = board.BiosVersion ?? "";
            TxtBiosDetail.Text = (vendor.Length == 0 && version.Length == 0) ? ""
                : board.BiosDate is { } bd
                    ? Loc.Tf("hw.bios.dated", vendor, version, HardwareText.LocalDate(bd))
                    : Loc.Tf("hw.bios", vendor, version);
        }
        var monitors = inv.Monitors
            .Select(m => m.Name ?? m.ManufacturerCode)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .ToList();
        TxtMonitors.Text = monitors.Count > 0 ? Loc.Tf("hw.monitors", string.Join(", ", monitors)) : "";
        TxtMonitors.Visibility = monitors.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // OS
        TxtOsLabel.Text = inv.OsLabel ?? unavailable;
        TxtOsUptime.Text = Loc.Tf("hw.os.uptime", HardwareText.FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64)));
    }

    // ---------------------------------------------------------------- stability (crash events)

    private void ApplyCrashSummary()
    {
        var services = App.Services;
        var c = services?.CrashSummary;
        StabilityRows.Children.Clear();
        if (c == null)
        {
            DotStability.Status = StatusKind.Unknown;
            TxtStabilitySummary.Text = Loc.T("system.loading");
            TxtStabilitySummary.ToolTip = null;
            TxtStabilityWindow.Text = "";
            return;
        }
        int days = (int)Math.Round((c.ToUtc - c.FromUtc).TotalDays);
        TxtStabilityWindow.Text = Loc.Tf("hw.stability.window", days, HardwareText.LocalTime(services!.CrashSummaryUtc));
        if (c.Reason != UnavailableReason.None)
        {
            DotStability.Status = StatusKind.Unknown;
            TxtStabilitySummary.Text = Loc.T("hw.stability.unavailable");
            TxtStabilitySummary.ToolTip = HardwareText.ReasonText(c.Reason);
            return;
        }
        TxtStabilitySummary.ToolTip = null;
        int total = c.Total;
        bool serious = c.Count(CrashEventKind.UnexpectedShutdown) > 0 || c.Count(CrashEventKind.WheaFatal) > 0 || c.Count(CrashEventKind.DisplayTdr) > 0
                       || c.Count(CrashEventKind.WheaCorrected) >= HealthEvaluator.WheaCorrectedWarnCount;
        DotStability.Status = serious ? StatusKind.Warn : StatusKind.Ok;
        TxtStabilitySummary.Text = total == 0 ? Loc.T("hw.stability.clean") : Loc.Plural("unit.events", total);
        AddStabilityRow(c, CrashEventKind.UnexpectedShutdown, "hw.stability.shutdowns", StatusKind.Warn);
        AddStabilityRow(c, CrashEventKind.WheaFatal, "hw.stability.wheaFatal", StatusKind.Warn);
        AddStabilityRow(c, CrashEventKind.DisplayTdr, "hw.stability.tdr", StatusKind.Warn);
        AddStabilityRow(c, CrashEventKind.WheaCorrected, "hw.stability.wheaCorrected",
            c.Count(CrashEventKind.WheaCorrected) >= HealthEvaluator.WheaCorrectedWarnCount ? StatusKind.Warn : StatusKind.Unknown);
        AddStabilityRow(c, CrashEventKind.DisplayDriverError, "hw.stability.driverErrors", StatusKind.Unknown);
    }

    private void AddStabilityRow(CrashEventSummary c, CrashEventKind kind, string labelKey, StatusKind kindStatus)
    {
        int n = c.Count(kind);
        if (n == 0) return;
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var dot = new StatusDot { Status = kindStatus, Margin = new Thickness(0, 0, 8, 0), Label = Loc.T(labelKey) };
        DockPanel.SetDock(dot, Dock.Left);
        var last = c.Last(kind);
        string count = Loc.Plural("unit.events", n);
        var text = new TextBlock
        {
            Text = Loc.T(labelKey) + ": " + (last is { } l ? Loc.Tf("hw.stability.last", count, HardwareText.LocalDate(l)) : count),
            Style = (Style)FindResource("HwLine"),
            Margin = new Thickness(0),
            TextWrapping = TextWrapping.Wrap
        };
        row.Children.Add(dot);
        row.Children.Add(text);
        StabilityRows.Children.Add(row);
    }

    // ---------------------------------------------------------------- layout

    /// <summary>Two card columns from 900 DIP; one below.</summary>
    private void HardwareHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool two = e.NewSize.Width >= 900;
        if (two == _hwTwoColumns && _hwLayoutDone) return;
        _hwTwoColumns = two;
        LayoutHardwareCards();
    }

    private void LayoutHardwareCards()
    {
        _hwLayoutDone = true;
        if (_hwTwoColumns)
        {
            HwGap.Width = new GridLength(12);
            HwColRight.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            HwGap.Width = new GridLength(0);
            HwColRight.Width = new GridLength(0);
        }
        for (int i = 0; i < _hwCards.Length; i++)
        {
            var card = _hwCards[i];
            if (_hwTwoColumns)
            {
                Grid.SetRow(card, i / 2);
                Grid.SetColumn(card, (i % 2) * 2);
            }
            else
            {
                Grid.SetRow(card, i);
                Grid.SetColumn(card, 0);
            }
        }
    }

    // ---------------------------------------------------------------- legacy snapshot + processes

    /// <summary>Captures the PC snapshot on a worker thread (WMI can take a second) and shows a loading state meanwhile.</summary>
    private async void RefreshSnapshot(bool includeProcesses)
    {
        if (TxtSystemCpu == null) return;
        if (!IsLive)
        {
            _stale = true;
            return;
        }
        if (_busy) return;

        _busy = true;
        _stale = false;
        string loading = Loc.T("system.loading");
        TxtSystemCpu.Text = loading;
        TxtSystemRam.Text = loading;
        TxtSystemGpu.Text = loading;
        TxtSystemOs.Text = loading;

        SystemInfoSnapshot? snap = null;
        try
        {
            snap = await Task.Run(SystemInfoService.Capture).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "System snapshot");
        }
        finally
        {
            _busy = false;
        }

        ApplySnapshot(snap);
        if (includeProcesses)
            RefreshProcessList();
    }

    private void ApplySnapshot(SystemInfoSnapshot? snap)
    {
        string unavailable = Loc.T("system.unavailable");
        if (snap == null)
        {
            TxtSystemCpu.Text = unavailable;
            TxtSystemRam.Text = unavailable;
            TxtSystemGpu.Text = unavailable;
            TxtSystemOs.Text = unavailable;
            BarSystemRam.Visibility = Visibility.Collapsed;
            SystemDiskList.Children.Clear();
            TxtSystemDisksEmpty.Visibility = Visibility.Visible;
            return;
        }

        TxtSystemCpu.Text = snap.CpuName == null
            ? unavailable
            : Loc.Tf("system.cpu.value", snap.CpuName, snap.LogicalProcessors);
        if (snap.RamTotalBytes <= 0)
        {
            TxtSystemRam.Text = unavailable;
            BarSystemRam.Visibility = Visibility.Collapsed;
        }
        else
        {
            TxtSystemRam.Text = Loc.Tf("system.ram.value",
                SystemInfoService.FormatBytes(snap.RamAvailableBytes),
                SystemInfoService.FormatBytes(snap.RamTotalBytes));
            BarSystemRam.Maximum = snap.RamTotalBytes;
            BarSystemRam.Value = Math.Max(0, snap.RamTotalBytes - snap.RamAvailableBytes);
            BarSystemRam.Visibility = Visibility.Visible;
        }
        TxtSystemGpu.Text = string.IsNullOrWhiteSpace(snap.GpuLabel)
            ? unavailable
            : snap.GpuLabel!;
        TxtSystemOs.Text = snap.WindowsVersion ?? unavailable;
        // Power plan + Game Mode are acted on in Optimize; mirror the facts in the OS card footer.
        if (TxtSystemOs.Text.Length > 0 && snap.PowerPlan != null)
            TxtSystemOs.Text += " · " + Loc.Tf("system.power.current", ComboUi.PowerPlanStateLabel(snap.PowerPlan));
        if (snap.GameModeEnabled != null)
            TxtSystemOs.Text += " · " + Loc.T(snap.GameModeEnabled.Value ? "system.gameMode.on.short" : "system.gameMode.off");

        SystemDiskList.Children.Clear();
        if (snap.Disks.Count == 0)
            TxtSystemDisksEmpty.Visibility = Visibility.Visible;
        else
        {
            TxtSystemDisksEmpty.Visibility = Visibility.Collapsed;
            foreach (var d in snap.Disks)
            {
                var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                row.Children.Add(new TextBlock
                {
                    Text = Loc.Tf("system.disk.row", d.Name,
                        SystemInfoService.FormatBytes(d.FreeBytes),
                        SystemInfoService.FormatBytes(d.TotalBytes)),
                    Style = (Style)FindResource("Hint"),
                    Margin = new Thickness(0, 0, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });
                if (d.TotalBytes > 0)
                {
                    row.Children.Add(new ThinBar
                    {
                        Maximum = d.TotalBytes,
                        Value = Math.Max(0, d.TotalBytes - d.FreeBytes),
                        Label = d.Name
                    });
                }
                SystemDiskList.Children.Add(row);
            }
        }
    }

    /// <summary>Read-only CPU/RAM sample of the busiest processes; never runs while a snapshot or another sample is in flight.</summary>
    private async void RefreshProcessList()
    {
        if (ProcessList == null || _busy || _processBusy) return;
        _processBusy = true;
        BtnProcessRefresh.IsEnabled = false;
        try
        {
            var sample = await Task.Run(() => SystemInfoService.SampleTopProcesses(12, 180))
                .ConfigureAwait(true);
            var rows = sample
                .Select(r => new ProcessRowVm
                {
                    Title = Loc.Tf("system.process.row", r.Name, r.Pid),
                    CpuText = Loc.Tf("system.process.cpu", r.CpuPercent.ToString("0.0")),
                    RamText = SystemInfoService.FormatBytes(r.WorkingSetBytes)
                })
                .ToList();
            ProcessList.ItemsSource = rows;
            TxtProcessEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Process sample");
            Host.ShowToast(Loc.Tf("system.processes.fail", ex.Message));
        }
        finally
        {
            _processBusy = false;
            BtnProcessRefresh.IsEnabled = true;
        }
    }

    private void SystemRefresh_Click(object sender, RoutedEventArgs e) => RefreshSnapshot(includeProcesses: true);

    private void ProcessRefresh_Click(object sender, RoutedEventArgs e) => RefreshProcessList();

    /// <summary>Re-captures the inventory (WMI) and re-reads the crash log (throttled to once per 10 min).</summary>
    private void HwRefresh_Click(object sender, RoutedEventArgs e)
    {
        LoadInventory(force: true);
        App.Services?.RequestCrashSummaryRefresh();
    }

    private void EventViewer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("eventvwr.msc") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Open Event Viewer");
            Host.ShowToast(Loc.Tf("hw.stability.openEventViewer.fail", ex.Message));
        }
    }
}
