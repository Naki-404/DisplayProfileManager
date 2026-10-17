using Visor.Services;
using Visor.Services.Optimize;
using Xunit;

namespace Visor.Tests;

/// <summary>Recommendation rules fed by real diagnostics (inventory / sensors / health / crash log / Optimize status).</summary>
public class OptimizeRecommendationTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private const string Exe = @"C:\Games\Doom\DOOMx64.exe";

    private static GpuSensorReading Gpu(int? tempC = 60, GpuThrottleFlags? throttle = GpuThrottleFlags.None, string name = "RTX 4070") => new()
    {
        Name = name,
        Vendor = GpuVendor.Nvidia,
        UtilizationPercent = Sensor.Of(90),
        TemperatureC = tempC is { } t ? Sensor.Of(t) : Sensor.Missing<int>(UnavailableReason.QueryFailed),
        Throttle = throttle is { } f ? Sensor.Of(f) : Sensor.Missing<GpuThrottleFlags>(UnavailableReason.UnsupportedVendor),
        CoreClockMhz = Sensor.Of(1800),
        MemoryClockMhz = Sensor.Of(10000),
        VramUsedBytes = Sensor.Of(1L),
        PowerPercent = Sensor.Of(99.6)
    };

    private static SensorSnapshot Snap(GpuSensorReading? gpu = null, double? ramPercent = 50, long freeBytes = 500, long totalBytes = 1000) => new()
    {
        CpuTotalPercent = Sensor.Of(10.0),
        RamTotalBytes = 32L * 1024 * 1024 * 1024,
        RamUsedBytes = (long)(32L * 1024 * 1024 * 1024 * ((ramPercent ?? 0) / 100.0)),
        RamUsedPercent = ramPercent is { } p ? Sensor.Of(p) : Sensor.Missing<double>(UnavailableReason.QueryFailed),
        Gpus = gpu == null ? Array.Empty<GpuSensorReading>() : new[] { gpu },
        Drives = new[] { new DriveSensorReading { Name = "C:", TotalBytes = totalBytes, FreeBytes = freeBytes } }
    };

    private static CrashEventSummary Crashes(int shutdowns = 0, int wheaFatal = 0, int wheaCorrected = 0, int tdr = 0,
        UnavailableReason reason = UnavailableReason.None) => new()
    {
        FromUtc = Now.AddDays(-30),
        ToUtc = Now,
        Reason = reason,
        Buckets = new[]
        {
            new CrashEventBucket(CrashEventKind.UnexpectedShutdown, shutdowns, shutdowns > 0 ? Now.AddDays(-2) : null),
            new CrashEventBucket(CrashEventKind.WheaFatal, wheaFatal, wheaFatal > 0 ? Now.AddDays(-1) : null),
            new CrashEventBucket(CrashEventKind.WheaCorrected, wheaCorrected, wheaCorrected > 0 ? Now.AddDays(-3) : null),
            new CrashEventBucket(CrashEventKind.DisplayTdr, tdr, tdr > 0 ? Now.AddDays(-4) : null),
            new CrashEventBucket(CrashEventKind.DisplayDriverError, 0, null)
        }
    };

    private static HardwareInventory Inventory(DateTime? driverDate, string? version = "581.72") => new()
    {
        Gpus = new[] { new GpuInventory { Name = "RTX 4070", Vendor = GpuVendor.Nvidia, DriverDate = driverDate, DriverVersion = version } }
    };

    private static RecommendationContext Ctx() => new() { ProfileCount = 1, NowUtc = Now };

    private static IReadOnlyList<Recommendation> Eval(RecommendationContext ctx) => RecommendationEngine.Evaluate(ctx);

    // ---------------- GPU throttle ----------------

    [Theory]
    [InlineData(GpuThrottleFlags.Thermal)]
    [InlineData(GpuThrottleFlags.Power)]
    [InlineData(GpuThrottleFlags.InsufficientPower)]
    [InlineData(GpuThrottleFlags.Thermal | GpuThrottleFlags.Voltage)]
    public void Gpu_throttle_from_sensors_is_warning_with_evidence(GpuThrottleFlags flags)
    {
        var list = Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Sensors = Snap(Gpu(87, flags)) });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdGpuThrottle);
        Assert.Equal(RecommendationSeverity.Warning, r.Severity);
        Assert.Equal(RecommendationAction.OpenSystem, r.Action);
        Assert.Null(r.SafeActionId);
        Assert.Contains("temp: 87 °C", r.Evidence);
        Assert.Contains("gpu: RTX 4070", r.Evidence);
        Assert.Contains(r.Evidence, e => e.StartsWith("throttle: ") && e.Contains((flags & ~GpuThrottleFlags.Voltage).ToString()));
        Assert.Contains("power: 100 %", r.Evidence);
        Assert.Equal("rec.gpu.throttle.why", r.WhyKey);
        Assert.Equal("rec.gpu.throttle.risk", r.RiskKey);
        Assert.Equal("rec.none.undo", r.UndoKey);
        Assert.Equal("RTX 4070", r.BodyArgs[0]);
    }

    [Theory]
    [InlineData(GpuThrottleFlags.None)]
    [InlineData(GpuThrottleFlags.Voltage)]
    [InlineData(GpuThrottleFlags.NoLoad | GpuThrottleFlags.Battery | GpuThrottleFlags.Api)]
    public void Gpu_informational_flags_or_missing_sensor_do_not_recommend(GpuThrottleFlags flags)
    {
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Sensors = Snap(Gpu(95, flags)) }),
            r => r.Id == RecommendationEngine.IdGpuThrottle);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Sensors = Snap(Gpu(95, throttle: null)) }),
            r => r.Id == RecommendationEngine.IdGpuThrottle);
        Assert.DoesNotContain(Eval(Ctx()), r => r.Id == RecommendationEngine.IdGpuThrottle);
    }

    [Fact]
    public void Gpu_throttle_falls_back_to_health_report_when_no_live_snapshot()
    {
        var health = HealthEvaluator.Evaluate(null, Snap(Gpu(80, GpuThrottleFlags.Power)), null, Now);
        var list = Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Health = health });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdGpuThrottle);
        Assert.Contains("gpu: RTX 4070", r.Evidence);
        Assert.Contains("throttle: Power", r.Evidence);

        var ok = HealthEvaluator.Evaluate(null, Snap(Gpu(60)), null, Now);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Health = ok }),
            x => x.Id == RecommendationEngine.IdGpuThrottle);
    }

    // ---------------- Disk ----------------

    [Fact]
    public void Disk_below_ten_percent_free_is_warning_per_drive()
    {
        var list = Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Sensors = Snap(freeBytes: 72, totalBytes: 1000) });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdDiskLow);
        Assert.Equal(RecommendationSeverity.Warning, r.Severity);
        Assert.Equal(new[] { "C:", "7" }, r.BodyArgs);
        Assert.Contains("free: 7.2 %", r.Evidence);
        Assert.Contains("drive: C:", r.Evidence);
        Assert.Equal("rec.disk.low.why", r.WhyKey);
    }

    [Theory]
    [InlineData(100, 1000)]
    [InlineData(500, 1000)]
    [InlineData(0, 0)]
    public void Disk_at_or_above_ten_percent_or_unknown_size_is_silent(long free, long total)
    {
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Sensors = Snap(freeBytes: free, totalBytes: total) }),
            r => r.Id == RecommendationEngine.IdDiskLow);
    }

    // ---------------- Driver ----------------

    [Fact]
    public void Driver_older_than_twelve_months_is_info_without_action()
    {
        var list = Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Inventory = Inventory(Now.AddMonths(-13)) });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdDriverOld);
        Assert.Equal(RecommendationSeverity.Info, r.Severity);
        Assert.Equal(RecommendationAction.None, r.Action);
        Assert.Null(r.ActionKey);
        Assert.Equal(new[] { "RTX 4070", "581.72", "13" }, r.BodyArgs);
        Assert.Contains("age: 13 mo", r.Evidence);
        Assert.Contains("date: 2025-09-13", r.Evidence);
        Assert.Equal("rec.driver.old.why", r.WhyKey);
    }

    [Fact]
    public void Driver_recent_or_unknown_date_is_silent()
    {
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Inventory = Inventory(Now.AddMonths(-11)) }),
            r => r.Id == RecommendationEngine.IdDriverOld);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Inventory = Inventory(null) }),
            r => r.Id == RecommendationEngine.IdDriverOld);
    }

    [Fact]
    public void Driver_without_version_uses_date_in_body()
    {
        var list = Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Inventory = Inventory(Now.AddMonths(-24), version: null) });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdDriverOld);
        Assert.Equal("2024-10-13", r.BodyArgs[1]);
        Assert.Contains("driver: ?", r.Evidence);
    }

    // ---------------- Crash events ----------------

    [Fact]
    public void Crash_events_warn_with_counts_and_last_date()
    {
        var list = Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Crashes = Crashes(shutdowns: 2, wheaFatal: 1, wheaCorrected: 5) });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdCrashEvents);
        Assert.Equal(RecommendationSeverity.Warning, r.Severity);
        Assert.Equal(new[] { "3", "30" }, r.BodyArgs);
        Assert.Contains("kernelPower41: 2", r.Evidence);
        Assert.Contains("wheaFatal: 1", r.Evidence);
        Assert.Contains("wheaCorrected: 5", r.Evidence);
        Assert.Contains("window: 30 d", r.Evidence);
        Assert.Contains("last: 2026-10-12", r.Evidence);
        Assert.Equal(RecommendationAction.OpenSystem, r.Action);
    }

    [Fact]
    public void Crash_corrected_whea_counts_only_as_a_flood()
    {
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Crashes = Crashes(wheaCorrected: 99) }),
            r => r.Id == RecommendationEngine.IdCrashEvents);
        var r = Assert.Single(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Crashes = Crashes(wheaCorrected: 100) }),
            x => x.Id == RecommendationEngine.IdCrashEvents);
        Assert.Equal("100", r.BodyArgs[0]);
    }

    [Fact]
    public void Crash_tdr_only_log_or_unreadable_log_is_silent()
    {
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Crashes = Crashes(tdr: 3) }),
            r => r.Id == RecommendationEngine.IdCrashEvents);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Crashes = Crashes(shutdowns: 5, reason: UnavailableReason.AccessDenied) }),
            r => r.Id == RecommendationEngine.IdCrashEvents);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Crashes = Crashes() }),
            r => r.Id == RecommendationEngine.IdCrashEvents);
    }

    // ---------------- RAM ----------------

    [Fact]
    public void Ram_high_only_while_a_game_runs()
    {
        var list = Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, GameRunning = true, Sensors = Snap(ramPercent: 93) });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdRamHigh);
        Assert.Equal(RecommendationSeverity.Warning, r.Severity);
        Assert.Contains("used: 93 %", r.Evidence);
        Assert.Contains("total: 32 GB", r.Evidence);
        Assert.Empty(r.BodyArgs); // the live number is evidence, so a dismissal survives 93 → 94 %

        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, GameRunning = false, Sensors = Snap(ramPercent: 93) }),
            x => x.Id == RecommendationEngine.IdRamHigh);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, GameRunning = true, Sensors = Snap(ramPercent: 89.9) }),
            x => x.Id == RecommendationEngine.IdRamHigh);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, GameRunning = true, Sensors = Snap(ramPercent: null) }),
            x => x.Id == RecommendationEngine.IdRamHigh);
    }

    // ---------------- Game Bar ----------------

    [Fact]
    public void GameBar_on_with_profiles_is_tip_to_safe_action()
    {
        var list = Eval(new RecommendationContext { ProfileCount = 3, NowUtc = Now, GameBarBackgroundOn = true });
        var r = Assert.Single(list, x => x.Id == RecommendationEngine.IdGameBarOff);
        Assert.Equal(RecommendationSeverity.Tip, r.Severity);
        Assert.Equal(RecommendationAction.SafeActionGameBarOff, r.Action);
        Assert.Equal(GameBarSafeActionTarget.Id, r.SafeActionId);
        Assert.Equal(SafeActionScope.Global, r.Scope);
        Assert.Equal("optimize.gameBar.off.why", r.WhyKey);
        Assert.Equal("optimize.gameBar.off.risk", r.RiskKey);
        Assert.Equal("optimize.gameBar.off.undo", r.UndoKey);
        Assert.Contains("profiles: 3", r.Evidence);
    }

    [Fact]
    public void GameBar_silent_without_profiles_or_when_off_or_unknown()
    {
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 0, NowUtc = Now, GameBarBackgroundOn = true }),
            r => r.Id == RecommendationEngine.IdGameBarOff);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, GameBarBackgroundOn = false }),
            r => r.Id == RecommendationEngine.IdGameBarOff);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, GameBarBackgroundOn = null }),
            r => r.Id == RecommendationEngine.IdGameBarOff);
    }

    // ---------------- Mouse ----------------

    [Fact]
    public void Mouse_acceleration_on_is_tip_to_safe_action()
    {
        var r = Assert.Single(Eval(new RecommendationContext { ProfileCount = 0, NowUtc = Now, MouseAccelerationOn = true }),
            x => x.Id == RecommendationEngine.IdMouseAccel);
        Assert.Equal(RecommendationSeverity.Tip, r.Severity);
        Assert.Equal(RecommendationAction.SafeActionMouseAccelOff, r.Action);
        Assert.Equal(MouseAccelerationSafeActionTarget.Id, r.SafeActionId);
        Assert.Equal("optimize.mouseAccel.off.undo", r.UndoKey);

        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, MouseAccelerationOn = false }),
            x => x.Id == RecommendationEngine.IdMouseAccel);
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, MouseAccelerationOn = null }),
            x => x.Id == RecommendationEngine.IdMouseAccel);
    }

    // ---------------- FSO per game ----------------

    [Fact]
    public void Fso_enabled_for_a_game_profile_is_per_game_tip()
    {
        var ctx = new RecommendationContext
        {
            ProfileCount = 2,
            NowUtc = Now,
            FsoStatuses = new[]
            {
                new GameFsoStatus("DOOM", Exe, false),
                new GameFsoStatus("Done", @"C:\G\done.exe", true),
                new GameFsoStatus("Unknown", @"C:\G\unk.exe", null)
            }
        };
        var r = Assert.Single(Eval(ctx), x => x.Id == RecommendationEngine.IdFsoGame);
        Assert.Equal(RecommendationSeverity.Tip, r.Severity);
        Assert.Equal(RecommendationAction.SafeActionFsoForGame, r.Action);
        Assert.Equal(FullscreenOptimizationSafeActionTarget.ActionIdFor(Exe), r.SafeActionId);
        Assert.Equal(SafeActionScope.PerGame(Exe), r.Scope);
        Assert.Equal(new[] { "DOOM" }, r.BodyArgs);
        Assert.Contains("exe: " + Exe, r.Evidence);
        Assert.Equal("optimize.fso.why", r.WhyKey);
        Assert.Equal("optimize.fso.risk", r.RiskKey);
        Assert.Equal("optimize.fso.undo", r.UndoKey);
    }

    [Fact]
    public void Fso_two_games_are_separate_occurrences_with_separate_dismissals()
    {
        var ctx = new RecommendationContext
        {
            ProfileCount = 2,
            NowUtc = Now,
            FsoStatuses = new[] { new GameFsoStatus("A", @"C:\G\a.exe", false), new GameFsoStatus("B", @"C:\G\b.exe", false) }
        };
        var both = Eval(ctx).Where(x => x.Id == RecommendationEngine.IdFsoGame).ToList();
        Assert.Equal(2, both.Count);
        Assert.NotEqual(both[0].ContextHash, both[1].ContextHash);

        var store = new List<string>();
        RecommendationDismissals.Add(store, both[0], Now);
        var after = Eval(new RecommendationContext
        {
            ProfileCount = 2, NowUtc = Now, FsoStatuses = ctx.FsoStatuses,
            DismissedIds = new HashSet<string>(store, StringComparer.OrdinalIgnoreCase)
        }).Where(x => x.Id == RecommendationEngine.IdFsoGame).ToList();
        var left = Assert.Single(after);
        Assert.Equal("B", left.BodyArgs[0]);
    }

    [Fact]
    public void Fso_empty_profile_name_falls_back_to_exe()
    {
        var r = Assert.Single(Eval(new RecommendationContext
        {
            ProfileCount = 1, NowUtc = Now, FsoStatuses = new[] { new GameFsoStatus("", Exe, false) }
        }), x => x.Id == RecommendationEngine.IdFsoGame);
        Assert.Equal(Exe, r.BodyArgs[0]);
    }

    // ---------------- Ordering / dismissal ----------------

    [Fact]
    public void Diagnostics_rules_sort_warnings_before_tips_and_info()
    {
        var list = Eval(new RecommendationContext
        {
            ProfileCount = 1,
            NowUtc = Now,
            Sensors = Snap(Gpu(90, GpuThrottleFlags.Thermal)),
            Inventory = Inventory(Now.AddMonths(-20)),
            MouseAccelerationOn = true
        });
        Assert.Equal(RecommendationSeverity.Warning, list[0].Severity);
        Assert.Equal(RecommendationSeverity.Info, list[^1].Severity);
        Assert.Equal(RecommendationEngine.IdDriverOld, list[^1].Id);
    }

    [Fact]
    public void Diagnostics_dismissal_is_per_occurrence()
    {
        var ctx = new RecommendationContext { ProfileCount = 1, NowUtc = Now, Sensors = Snap(Gpu(90, GpuThrottleFlags.Thermal)) };
        var rec = Assert.Single(Eval(ctx), x => x.Id == RecommendationEngine.IdGpuThrottle);
        var store = new List<string>();
        RecommendationDismissals.Add(store, rec, Now);
        var dismissed = new HashSet<string>(store, StringComparer.OrdinalIgnoreCase);

        // Same GPU, same flags, different temperature → same occurrence (temperature is evidence, not an arg).
        Assert.DoesNotContain(Eval(new RecommendationContext
        {
            ProfileCount = 1, NowUtc = Now, DismissedIds = dismissed, Sensors = Snap(Gpu(95, GpuThrottleFlags.Thermal))
        }), x => x.Id == RecommendationEngine.IdGpuThrottle);

        // Different flags → new occurrence shows again.
        Assert.Contains(Eval(new RecommendationContext
        {
            ProfileCount = 1, NowUtc = Now, DismissedIds = dismissed, Sensors = Snap(Gpu(95, GpuThrottleFlags.Power))
        }), x => x.Id == RecommendationEngine.IdGpuThrottle);
    }

    [Fact]
    public void Every_diagnostics_recommendation_carries_evidence_and_key_triple()
    {
        var list = Eval(new RecommendationContext
        {
            ProfileCount = 2,
            NowUtc = Now,
            GameRunning = true,
            Sensors = Snap(Gpu(90, GpuThrottleFlags.Power), ramPercent: 95, freeBytes: 10, totalBytes: 1000),
            Inventory = Inventory(Now.AddMonths(-14)),
            Crashes = Crashes(shutdowns: 1),
            GameBarBackgroundOn = true,
            MouseAccelerationOn = true,
            FsoStatuses = new[] { new GameFsoStatus("DOOM", Exe, false) }
        });
        var ids = new[]
        {
            RecommendationEngine.IdGpuThrottle, RecommendationEngine.IdDiskLow, RecommendationEngine.IdDriverOld,
            RecommendationEngine.IdCrashEvents, RecommendationEngine.IdRamHigh, RecommendationEngine.IdGameBarOff,
            RecommendationEngine.IdMouseAccel, RecommendationEngine.IdFsoGame
        };
        foreach (var id in ids)
        {
            var r = Assert.Single(list, x => x.Id == id);
            Assert.NotEmpty(r.Evidence);
            Assert.False(string.IsNullOrEmpty(r.WhyKey), id);
            Assert.False(string.IsNullOrEmpty(r.RiskKey), id);
            Assert.False(string.IsNullOrEmpty(r.UndoKey), id);
            bool safe = r.Action is RecommendationAction.SafeActionGameBarOff or RecommendationAction.SafeActionMouseAccelOff
                or RecommendationAction.SafeActionFsoForGame;
            Assert.Equal(safe, r.SafeActionId != null);
        }
    }

    [Fact]
    public void Legacy_rules_and_null_diagnostics_are_unchanged()
    {
        var list = Eval(new RecommendationContext { ProfileCount = 0, GameModeEnabled = false, NowUtc = Now });
        Assert.Equal(2, list.Count);
        Assert.Contains(list, r => r.Id == RecommendationEngine.IdEmptyLibrary);
        Assert.Contains(list, r => r.Id == RecommendationEngine.IdGameModeOff);
        Assert.All(list, r => Assert.Empty(r.Evidence));
    }

    // ---------------- PC tweaks: startup apps / cleanup / HAGS ----------------

    private static Visor.Services.Optimize.Pc.StartupEntry Startup(string name, bool enabled, long? size) => new()
    {
        Location = Visor.Services.Optimize.Pc.StartupLocation.UserRun,
        Name = name,
        DisplayName = name,
        Command = "\"C:\\Apps\\" + name + ".exe\"",
        Enabled = enabled,
        FileSizeBytes = size
    };

    [Fact]
    public void Many_enabled_startup_apps_is_info_with_names_and_sizes_as_evidence()
    {
        var apps = Enumerable.Range(1, 10).Select(i => Startup("App" + i, true, i * 1024L * 1024)).ToList();
        apps.Add(Startup("Disabled", false, 999L * 1024 * 1024));
        apps.Add(Startup("NoFile", true, null));
        var r = Assert.Single(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, StartupApps = apps }),
            x => x.Id == RecommendationEngine.IdStartupMany);
        Assert.Equal(RecommendationSeverity.Info, r.Severity);
        Assert.Equal(RecommendationAction.OpenStartupApps, r.Action);
        Assert.Null(r.SafeActionId);
        Assert.Equal("11", r.BodyArgs[0]);
        Assert.Equal("enabled: 11/12", r.Evidence[0]);
        Assert.Equal("App10: 10 MB", r.Evidence[1]);                   // largest first
        Assert.Equal(1 + RecommendationEngine.StartupEvidenceMax, r.Evidence.Length);
        Assert.DoesNotContain(r.Evidence, e => e.StartsWith("Disabled"));
        Assert.Equal("optimize.startup.undo", r.UndoKey);
    }

    [Fact]
    public void Few_startup_apps_or_unknown_list_do_not_recommend()
    {
        var nine = Enumerable.Range(1, 9).Select(i => Startup("A" + i, true, 1)).ToList();
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, StartupApps = nine }),
            r => r.Id == RecommendationEngine.IdStartupMany);
        Assert.DoesNotContain(Eval(Ctx()), r => r.Id == RecommendationEngine.IdStartupMany);
    }

    private static Visor.Services.Optimize.Pc.CleanupScanResult Scan(long tempBytes, long shaderBytes) => new(new[]
    {
        new Visor.Services.Optimize.Pc.CleanupCategoryScan { Category = Visor.Services.Optimize.Pc.CleanupCategory.UserTemp, Bytes = tempBytes, Files = 3 },
        new Visor.Services.Optimize.Pc.CleanupCategoryScan { Category = Visor.Services.Optimize.Pc.CleanupCategory.DirectXShaderCache, Bytes = shaderBytes, Files = 9 },
        new Visor.Services.Optimize.Pc.CleanupCategoryScan { Category = Visor.Services.Optimize.Pc.CleanupCategory.DeliveryOptimization, InfoOnly = true, RequiresAdmin = true }
    }, Now);

    [Fact]
    public void Low_disk_with_measured_cache_bytes_is_a_cleanup_tip()
    {
        const long gb = 1024L * 1024 * 1024;
        var ctx = new RecommendationContext
        {
            ProfileCount = 1, NowUtc = Now,
            Sensors = Snap(freeBytes: 50, totalBytes: 1000),
            CleanupScan = Scan(gb / 2, gb),
            CleanupDrive = "C:\\"
        };
        var r = Assert.Single(Eval(ctx), x => x.Id == RecommendationEngine.IdCleanupDisk);
        Assert.Equal(RecommendationSeverity.Tip, r.Severity);
        Assert.Equal(RecommendationAction.OpenCleanup, r.Action);
        Assert.Null(r.SafeActionId);
        Assert.Equal(new[] { "C:" }, r.BodyArgs);                       // bytes stay out of the dismissal hash
        Assert.Contains("reclaimable: 1.5 GB", r.Evidence);
        Assert.Contains("DirectXShaderCache: 1 GB", r.Evidence);
        Assert.Contains("UserTemp: 512 MB", r.Evidence);
        Assert.DoesNotContain(r.Evidence, e => e.StartsWith("DeliveryOptimization"));
        Assert.Contains(Eval(ctx), x => x.Id == RecommendationEngine.IdDiskLow); // the warning stays separate
    }

    [Fact]
    public void Cleanup_tip_needs_low_disk_on_the_cache_drive_and_enough_bytes()
    {
        const long mb = 1024L * 1024;
        RecommendationContext C(long free, long bytes, string drive) => new()
        {
            ProfileCount = 1, NowUtc = Now, Sensors = Snap(freeBytes: free, totalBytes: 1000), CleanupScan = Scan(bytes, 0), CleanupDrive = drive
        };
        Assert.DoesNotContain(Eval(C(500, 5000 * mb, "C:\\")), r => r.Id == RecommendationEngine.IdCleanupDisk); // 50 % free
        Assert.DoesNotContain(Eval(C(50, 100 * mb, "C:\\")), r => r.Id == RecommendationEngine.IdCleanupDisk);   // too little to matter
        Assert.DoesNotContain(Eval(C(50, 5000 * mb, "D:\\")), r => r.Id == RecommendationEngine.IdCleanupDisk);  // other drive
        Assert.Contains(Eval(C(50, 5000 * mb, "c:")), r => r.Id == RecommendationEngine.IdCleanupDisk);
    }

    [Fact]
    public void Hags_off_on_supported_gpu_is_info_with_the_elevated_safe_action()
    {
        var hags = new Visor.Services.Optimize.Pc.HagsStatus
        {
            RegistryValue = 1, RegistryReadable = true, Supported = true, RunningOn = false, MaxWddmVersion = 3100
        };
        var r = Assert.Single(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Hags = hags }),
            x => x.Id == RecommendationEngine.IdHagsOff);
        Assert.Equal(RecommendationSeverity.Info, r.Severity);
        Assert.Equal(RecommendationAction.SafeActionHagsOn, r.Action);
        Assert.Equal(Visor.Services.Optimize.Pc.HagsTarget.OnId, r.SafeActionId);
        Assert.Contains("HwSchMode=1", r.Evidence);
        Assert.Contains("wddm=3.1", r.Evidence);
        Assert.Equal("optimize.hagsMode.undo", r.UndoKey);
    }

    [Theory]
    [InlineData(null, null, 3200, 1)]    // caps query unavailable → skip
    [InlineData(false, null, 3200, 1)]   // GPU cannot do it
    [InlineData(true, true, 3200, 2)]    // already on
    [InlineData(true, false, 3200, 2)]   // turned on, restart pending
    [InlineData(true, false, 2600, 1)]   // pre-2.7 driver
    public void Hags_rule_is_quiet_when_unknown_unsupported_on_or_pending(bool? supported, bool? running, int wddm, int registry)
    {
        var hags = new Visor.Services.Optimize.Pc.HagsStatus
        {
            RegistryValue = registry, RegistryReadable = true, Supported = supported, RunningOn = running, MaxWddmVersion = wddm
        };
        Assert.DoesNotContain(Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, Hags = hags }),
            r => r.Id == RecommendationEngine.IdHagsOff);
    }

    [Fact]
    public void Ultimate_plan_is_never_recommended()
    {
        var list = Eval(new RecommendationContext { ProfileCount = 1, NowUtc = Now, PowerPlanAliasOrGuid = "balanced" });
        Assert.DoesNotContain(list, r => r.Id.Contains("ultimate", StringComparison.OrdinalIgnoreCase));
    }
}
