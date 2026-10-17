using Visor.Services;
using Visor.Services.Optimize;
using Visor.Services.Optimize.Pc;
using Xunit;

namespace Visor.Tests;

/// <summary>Records the live SystemParametersInfo / broadcast calls instead of touching the session.</summary>
internal sealed class FakeVisualEffectsLive : IVisualEffectsLive
{
    public readonly List<(uint Spi, bool Value)> Preferences = new();
    public readonly List<bool> MinAnimate = new();
    public int Broadcasts;

    public int Calls => Preferences.Count + MinAnimate.Count + Broadcasts;

    public void SetPreference(uint spiSetAction, bool value) => Preferences.Add((spiSetAction, value));
    public void SetMinimizeAnimation(bool on) => MinAnimate.Add(on);
    public void BroadcastSettingChange() => Broadcasts++;

    public void Clear()
    {
        Preferences.Clear();
        MinAnimate.Clear();
        Broadcasts = 0;
    }
}

public class InAppVisualEffectsTests
{
    private static readonly byte[] TypicalMask = { 0x9E, 0x3E, 0x07, 0x80, 0x12, 0x00, 0x00, 0x00 };

    private static SafeActionService Build(FakeOptimizeRegistry reg, FakeVisualEffectsLive live) =>
        new(new SafeActionJournal(null), new ISafeActionTarget[] { new VisualEffectsSafeActionTarget(reg, live) });

    private static FakeOptimizeRegistry Typical() => new FakeOptimizeRegistry()
        .Set(VisualEffectsSafeActionTarget.PreferencesMask, RegValue.Binary(TypicalMask))
        .Str(VisualEffectsSafeActionTarget.MinAnimate, "1");

    [Theory]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETMENUANIMATION, 0, 0x02)]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETCOMBOBOXANIMATION, 0, 0x04)]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETLISTBOXSMOOTHSCROLLING, 0, 0x08)]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETMENUFADE, 1, 0x02)]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETSELECTIONFADE, 1, 0x04)]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETTOOLTIPANIMATION, 1, 0x08)]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETTOOLTIPFADE, 1, 0x10)]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETCURSORSHADOW, 1, 0x20)]
    [InlineData(VisualEffectsSafeActionTarget.SPI_SETDROPSHADOW, 2, 0x04)]
    public void Mask_bits_follow_the_documented_UserPreferencesMask_layout(uint spi, int expectedByte, int expectedBit)
    {
        var (b, bit) = VisualEffectsSafeActionTarget.MaskBit(spi);
        Assert.Equal(expectedByte, b);
        Assert.Equal(expectedBit, bit);
    }

    [Fact]
    public void Performance_mask_clears_only_the_listed_effects_and_keeps_UIEffects_hot_tracking_and_tail_bytes()
    {
        var m = VisualEffectsSafeActionTarget.PerformanceMask(TypicalMask);
        Assert.Equal(new byte[] { 0x90, 0x00, 0x03, 0x80, 0x12, 0x00, 0x00, 0x00 }, m);
        Assert.Equal(TypicalMask.Length, m.Length);
        Assert.Equal(0x9E, TypicalMask[0]); // input untouched
    }

    [Fact]
    public void Apply_writes_every_value_applies_live_and_journals_the_exact_previous_state()
    {
        var reg = Typical();
        var live = new FakeVisualEffectsLive();
        var svc = Build(reg, live);
        Assert.True(VisualEffectsSafeActionTarget.ReadEffectsOn(reg));

        var r = svc.Apply(VisualEffectsSafeActionTarget.Id);

        Assert.Equal(SafeActionResult.Applied, r.Result);
        Assert.Equal(RegValue.Binary(new byte[] { 0x90, 0x00, 0x03, 0x80, 0x12, 0x00, 0x00, 0x00 }), reg.Get(VisualEffectsSafeActionTarget.PreferencesMask));
        Assert.Equal(RegValue.String("0"), reg.Get(VisualEffectsSafeActionTarget.MinAnimate));
        Assert.Equal(0, reg.Get(VisualEffectsSafeActionTarget.TaskbarAnimations)!.AsDword);
        Assert.Equal(0, reg.Get(VisualEffectsSafeActionTarget.AeroPeek)!.AsDword);
        Assert.Equal(3, reg.Get(VisualEffectsSafeActionTarget.VisualFxSetting)!.AsDword);
        Assert.False(VisualEffectsSafeActionTarget.ReadEffectsOn(reg));

        // Live: every listed SPI with "off", minimize animation off, one broadcast.
        Assert.Equal(VisualEffectsSafeActionTarget.TurnedOff, live.Preferences.Select(p => p.Spi));
        Assert.All(live.Preferences, p => Assert.False(p.Value));
        Assert.Equal(new[] { false }, live.MinAnimate);
        Assert.Equal(1, live.Broadcasts);

        var entry = svc.Journal.Get(VisualEffectsSafeActionTarget.Key)!;
        Assert.Equal(VisualEffectsSafeActionTarget.Id, entry.ActionId);
        Assert.True(RegistryValueSet.TryParse(entry.PreviousState, out var prev));
        Assert.Equal(RegValue.Binary(TypicalMask), prev.Get(VisualEffectsSafeActionTarget.PreferencesMask));
        Assert.Equal(RegValue.String("1"), prev.Get(VisualEffectsSafeActionTarget.MinAnimate));
        Assert.Null(prev.Get(VisualEffectsSafeActionTarget.TaskbarAnimations));
        Assert.Null(prev.Get(VisualEffectsSafeActionTarget.AeroPeek));
        Assert.Null(prev.Get(VisualEffectsSafeActionTarget.VisualFxSetting));
    }

    [Fact]
    public void Undo_restores_exact_bytes_deletes_absent_values_and_reapplies_the_old_state_live()
    {
        var reg = Typical();
        var live = new FakeVisualEffectsLive();
        var svc = Build(reg, live);
        Assert.Equal(SafeActionResult.Applied, svc.Apply(VisualEffectsSafeActionTarget.Id).Result);
        live.Clear();

        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(VisualEffectsSafeActionTarget.Key).Result);

        Assert.Equal(RegValue.Binary(TypicalMask), reg.Get(VisualEffectsSafeActionTarget.PreferencesMask));
        Assert.Equal(RegValue.String("1"), reg.Get(VisualEffectsSafeActionTarget.MinAnimate));
        Assert.False(reg.Has(VisualEffectsSafeActionTarget.TaskbarAnimations));
        Assert.False(reg.Has(VisualEffectsSafeActionTarget.AeroPeek));
        Assert.False(reg.Has(VisualEffectsSafeActionTarget.VisualFxSetting));
        Assert.Equal(2, reg.Values.Count);

        // Live re-apply uses the restored bits: everything in the typical mask was on.
        Assert.All(live.Preferences, p => Assert.True(p.Value));
        Assert.Equal(VisualEffectsSafeActionTarget.TurnedOff.Count, live.Preferences.Count);
        Assert.Equal(new[] { true }, live.MinAnimate);
        Assert.Equal(1, live.Broadcasts);
        Assert.False(svc.CanUndo(VisualEffectsSafeActionTarget.Key));
    }

    [Fact]
    public void Undo_restores_a_partly_custom_state_bit_for_bit()
    {
        // Cursor shadow and drop shadow were already off, menu fade on.
        var custom = new byte[] { 0x9E, 0x1E, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 };
        var reg = new FakeOptimizeRegistry()
            .Set(VisualEffectsSafeActionTarget.PreferencesMask, RegValue.Binary(custom))
            .Str(VisualEffectsSafeActionTarget.MinAnimate, "1")
            .Dword(VisualEffectsSafeActionTarget.TaskbarAnimations, 1)
            .Dword(VisualEffectsSafeActionTarget.VisualFxSetting, 0);
        var live = new FakeVisualEffectsLive();
        var svc = Build(reg, live);
        svc.Apply(VisualEffectsSafeActionTarget.Id);
        live.Clear();

        svc.Undo(VisualEffectsSafeActionTarget.Key);

        Assert.Equal(RegValue.Binary(custom), reg.Get(VisualEffectsSafeActionTarget.PreferencesMask));
        Assert.Equal(1, reg.Get(VisualEffectsSafeActionTarget.TaskbarAnimations)!.AsDword);
        Assert.Equal(0, reg.Get(VisualEffectsSafeActionTarget.VisualFxSetting)!.AsDword);
        Assert.False(reg.Has(VisualEffectsSafeActionTarget.AeroPeek));
        var byAction = live.Preferences.ToDictionary(p => p.Spi, p => p.Value);
        Assert.False(byAction[VisualEffectsSafeActionTarget.SPI_SETCURSORSHADOW]);
        Assert.False(byAction[VisualEffectsSafeActionTarget.SPI_SETDROPSHADOW]);
        Assert.True(byAction[VisualEffectsSafeActionTarget.SPI_SETMENUFADE]);
    }

    [Fact]
    public void Second_apply_is_a_NoOp_without_writes_or_live_calls()
    {
        var reg = Typical();
        var live = new FakeVisualEffectsLive();
        var svc = Build(reg, live);
        svc.Apply(VisualEffectsSafeActionTarget.Id);
        var journaled = svc.Journal.Get(VisualEffectsSafeActionTarget.Key)!.PreviousState;
        int writes = reg.Writes;
        live.Clear();

        Assert.Equal(SafeActionResult.NoOp, svc.Apply(VisualEffectsSafeActionTarget.Id).Result);
        Assert.Equal(writes, reg.Writes);
        Assert.Equal(0, live.Calls);
        Assert.Equal(journaled, svc.Journal.Get(VisualEffectsSafeActionTarget.Key)!.PreviousState); // backup kept
    }

    [Fact]
    public void Already_at_performance_set_is_NoOp_even_when_the_dialog_marker_differs()
    {
        var reg = new FakeOptimizeRegistry()
            .Set(VisualEffectsSafeActionTarget.PreferencesMask, RegValue.Binary(VisualEffectsSafeActionTarget.PerformanceMask(TypicalMask)))
            .Str(VisualEffectsSafeActionTarget.MinAnimate, "0")
            .Dword(VisualEffectsSafeActionTarget.TaskbarAnimations, 0)
            .Dword(VisualEffectsSafeActionTarget.AeroPeek, 0)
            .Dword(VisualEffectsSafeActionTarget.VisualFxSetting, 2);
        var live = new FakeVisualEffectsLive();
        Assert.Equal(SafeActionResult.NoOp, Build(reg, live).Apply(VisualEffectsSafeActionTarget.Id).Result);
        Assert.Equal(0, reg.Writes);
        Assert.Equal(0, live.Calls);
    }

    [Fact]
    public void Absent_mask_uses_the_Windows_default_and_undo_deletes_it_again()
    {
        var reg = new FakeOptimizeRegistry(); // nothing set: everything at the Windows default (on)
        var live = new FakeVisualEffectsLive();
        var svc = Build(reg, live);
        Assert.True(VisualEffectsSafeActionTarget.ReadEffectsOn(reg));

        Assert.Equal(SafeActionResult.Applied, svc.Apply(VisualEffectsSafeActionTarget.Id).Result);
        Assert.Equal(RegValue.Binary(VisualEffectsSafeActionTarget.PerformanceMask(VisualEffectsSafeActionTarget.DefaultMask)),
            reg.Get(VisualEffectsSafeActionTarget.PreferencesMask));

        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(VisualEffectsSafeActionTarget.Key).Result);
        Assert.Empty(reg.Values);
    }

    [Fact]
    public void Malformed_mask_is_never_rewritten_and_the_other_values_still_apply()
    {
        var weird = RegValue.String("not binary");
        var reg = new FakeOptimizeRegistry()
            .Set(VisualEffectsSafeActionTarget.PreferencesMask, weird)
            .Str(VisualEffectsSafeActionTarget.MinAnimate, "1");
        var live = new FakeVisualEffectsLive();
        var svc = Build(reg, live);

        Assert.Equal(SafeActionResult.Applied, svc.Apply(VisualEffectsSafeActionTarget.Id).Result);
        Assert.Equal(weird, reg.Get(VisualEffectsSafeActionTarget.PreferencesMask));
        Assert.Equal(RegValue.String("0"), reg.Get(VisualEffectsSafeActionTarget.MinAnimate));
        Assert.Empty(live.Preferences);              // no guessing which bits are live
        Assert.Equal(new[] { false }, live.MinAnimate);
    }

    [Fact]
    public void Writes_that_do_not_stick_fail_verification()
    {
        var reg = Typical();
        reg.IgnoreWrites = true;
        var r = Build(reg, new FakeVisualEffectsLive()).Apply(VisualEffectsSafeActionTarget.Id);
        Assert.Equal(SafeActionResult.Failed, r.Result);
        Assert.Equal(SafeActionCodes.VerifyMismatch, r.Code);
    }

    [Fact]
    public void Undo_after_an_outside_change_reports_a_conflict()
    {
        var reg = Typical();
        var svc = Build(reg, new FakeVisualEffectsLive());
        svc.Apply(VisualEffectsSafeActionTarget.Id);
        reg.Dword(VisualEffectsSafeActionTarget.AeroPeek, 1); // user re-enabled Peek in Windows

        Assert.Equal(SafeActionUndoResult.Conflict, svc.Undo(VisualEffectsSafeActionTarget.Key).Result);
        Assert.Equal(SafeActionUndoResult.Undone, svc.Undo(VisualEffectsSafeActionTarget.Key, confirmedConflict: true).Result);
        Assert.Equal(RegValue.Binary(TypicalMask), reg.Get(VisualEffectsSafeActionTarget.PreferencesMask));
    }

    [Fact]
    public void Readability_settings_are_not_owned_by_the_target()
    {
        var reg = Typical();
        var target = new VisualEffectsSafeActionTarget(reg, new FakeVisualEffectsLive());
        Assert.True(RegistryValueSet.TryParse(target.ReadState(), out var set));
        var names = set.Items.Select(kv => kv.Key.ValueName).ToList();
        Assert.Equal(new[] { "UserPreferencesMask", "MinAnimate", "TaskbarAnimations", "EnableAeroPeek", "VisualFXSetting" }, names);
        Assert.DoesNotContain("FontSmoothing", names);
        Assert.DoesNotContain("IconsOnly", names);
        Assert.DoesNotContain("DragFullWindows", names);
        Assert.DoesNotContain("ListviewShadow", names);
        Assert.All(set.Items, kv => Assert.False(kv.Key.KeyPath.StartsWith("SYSTEM", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void Target_is_HKCU_only_and_has_honest_texts_in_both_locales()
    {
        ISafeActionTarget t = new VisualEffectsSafeActionTarget(new FakeOptimizeRegistry(), new FakeVisualEffectsLive());
        Assert.False(t.RequiresElevation);
        Assert.False(t.BlockedDuringGameSession);
        Assert.Equal("optimize.visualFx", t.LocPrefix);
        foreach (var key in new[] { t.TitleKey, t.WhyKey, t.RiskKey, t.UndoKey, "optimize.visualFx.fpsNote" })
            Assert.True(Loc.Has(key), key);
        var before = Loc.Locale;
        try
        {
            Loc.SetLocale("en");
            Assert.Contains("FPS", Loc.T(t.WhyKey));
            Assert.DoesNotContain("boost", Loc.T(t.WhyKey), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no effect on in-game FPS", Loc.T("optimize.visualFx.fpsNote"));
        }
        finally
        {
            Loc.SetLocale(before);
        }
    }

    [Fact]
    public void PcTweaks_registers_the_visual_effects_target_and_the_delivery_optimization_service()
    {
        var pc = PcTweaks.Create(null, new PcFakeBroker(), _ => true);
        Assert.Contains(pc.Targets, t => t.ActionId == VisualEffectsSafeActionTarget.Id && t.JournalKey == VisualEffectsSafeActionTarget.Key);
        Assert.NotNull(pc.DeliveryOptimization);
        Assert.Equal(VisualEffectsSafeActionTarget.Key, OptimizeStatus.VisualFxId);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("s:1", true)]
    [InlineData("s:0", false)]
    [InlineData("s: 0 ", false)]
    [InlineData("d:0", false)]
    [InlineData("s:x", null)]
    public void MinAnimate_parsing(string? serialized, bool? expected)
    {
        RegValue? v = null;
        if (serialized != null) Assert.True(RegValue.TryParse(serialized, out v));
        Assert.Equal(expected, VisualEffectsSafeActionTarget.MinAnimateOn(v));
    }
}
