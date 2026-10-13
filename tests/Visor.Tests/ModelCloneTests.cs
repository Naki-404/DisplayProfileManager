using System.Reflection;
using System.Text.Json;
using Visor.Models;
using Xunit;

namespace Visor.Tests;

public class ModelCloneTests
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>Non-UI properties that Clone() is not expected to copy.</summary>
    private static readonly HashSet<string> ProfileExclusions = new(StringComparer.Ordinal)
    {
        nameof(GameProfile.TileBrush), nameof(GameProfile.Icon), nameof(GameProfile.Glyph)
    };

    // ------------------------------------------------------------------ GameProfile

    [Fact]
    public void GameProfile_Clone_copies_every_settable_property()
    {
        var original = new GameProfile();
        var touched = FillWithNonDefaults(original, ProfileExclusions);
        Assert.True(touched.Count >= 25, $"expected to populate most properties, got {touched.Count}");

        var clone = original.Clone();

        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(clone, Json));
        foreach (var name in touched)
        {
            var prop = typeof(GameProfile).GetProperty(name)!;
            var a = prop.GetValue(original);
            var b = prop.GetValue(clone);
            if (prop.PropertyType == typeof(string) || prop.PropertyType.IsValueType || Nullable.GetUnderlyingType(prop.PropertyType) != null)
                Assert.True(Equals(a, b), $"{name} not copied");
            else
                Assert.True(a != null && b != null, $"{name} null on one side");
        }
    }

    [Fact]
    public void GameProfile_Clone_is_deep()
    {
        var original = new GameProfile
        {
            ProcessAliases = { "a.exe" },
            Companions = { new CompanionApp { Path = @"C:\t\x.exe" } },
            Presets = { new QuickPreset { Name = "P" } },
            Color = new ColorSettings { Brightness = 0.6 },
            ColorLowLevel = new ColorSettings { Gamma = 1.4 },
            ColorDriver = new ColorSettings { Vibrance = 70 },
            Session = new SessionExtras { MonitorBrightness = 55 }
        };

        var clone = original.Clone();

        Assert.NotSame(original.ProcessAliases, clone.ProcessAliases);
        Assert.NotSame(original.Companions, clone.Companions);
        Assert.NotSame(original.Companions[0], clone.Companions[0]);
        Assert.NotSame(original.Presets, clone.Presets);
        Assert.NotSame(original.Presets[0], clone.Presets[0]);
        Assert.NotSame(original.Presets[0].Color, clone.Presets[0].Color);
        Assert.NotSame(original.Color, clone.Color);
        Assert.NotSame(original.ColorLowLevel, clone.ColorLowLevel);
        Assert.NotSame(original.ColorDriver, clone.ColorDriver);
        Assert.NotSame(original.Session, clone.Session);

        clone.ProcessAliases.Add("b.exe");
        clone.Companions[0].Path = @"C:\t\y.exe";
        clone.Companions.Add(new CompanionApp());
        clone.Presets[0].Name = "Q";
        clone.Presets[0].Color.Brightness = 0.1;
        clone.Color.Brightness = 0.9;
        clone.ColorLowLevel!.Gamma = 2;
        clone.ColorDriver!.Vibrance = 10;
        clone.Session.MonitorBrightness = 1;

        Assert.Single(original.ProcessAliases);
        Assert.Single(original.Companions);
        Assert.Equal(@"C:\t\x.exe", original.Companions[0].Path);
        Assert.Equal("P", original.Presets[0].Name);
        Assert.Equal(0.5, original.Presets[0].Color.Brightness);
        Assert.Equal(0.6, original.Color.Brightness);
        Assert.Equal(1.4, original.ColorLowLevel!.Gamma);
        Assert.Equal(70, original.ColorDriver!.Vibrance);
        Assert.Equal(55, original.Session.MonitorBrightness);
    }

    [Fact]
    public void GameProfile_Clone_tolerates_null_collections()
    {
        var original = new GameProfile
        {
            ProcessAliases = null!, Companions = null!, Presets = null!, Color = null!, Session = null!
        };
        var clone = original.Clone();
        Assert.NotNull(clone.ProcessAliases);
        Assert.NotNull(clone.Companions);
        Assert.NotNull(clone.Presets);
        Assert.NotNull(clone.Color);
        Assert.NotNull(clone.Session);
    }

    // ------------------------------------------------------------------ CompanionApp

    [Fact]
    public void CompanionApp_Clone_copies_every_settable_property()
    {
        var original = new CompanionApp();
        var touched = FillWithNonDefaults(original, new HashSet<string>());
        Assert.Equal(typeof(CompanionApp).GetProperties().Count(p => p.CanWrite), touched.Count);

        var clone = original.Clone();

        Assert.NotSame(original, clone);
        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(clone, Json));
        clone.Path = "changed";
        Assert.NotEqual("changed", original.Path);
    }

    // ------------------------------------------------------------------ QuickPreset

    [Fact]
    public void QuickPreset_CloneOf_copies_every_settable_property_and_is_deep()
    {
        var original = new QuickPreset();
        var touched = FillWithNonDefaults(original, new HashSet<string>());
        Assert.Equal(typeof(QuickPreset).GetProperties().Count(p => p.CanWrite), touched.Count);

        var clone = QuickPreset.CloneOf(original);

        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(clone, Json));
        Assert.NotSame(original.Color, clone.Color);
        Assert.NotSame(original.ColorLowLevel, clone.ColorLowLevel);
        Assert.NotSame(original.ColorDriver, clone.ColorDriver);

        clone.Color.Gamma = 5;
        clone.ColorLowLevel!.Gamma = 5;
        clone.ColorDriver!.Gamma = 2.5;
        Assert.NotEqual(5, original.Color.Gamma);
        Assert.NotEqual(5, original.ColorLowLevel!.Gamma);
        Assert.NotEqual(2.5, original.ColorDriver!.Gamma);
    }

    // ------------------------------------------------------------------ other Clone()s

    [Fact]
    public void ColorSettings_SessionExtras_DefaultSettings_UiPreferences_Clone_copy_all()
    {
        AssertCloneCopiesAll(new ColorSettings(), c => c.Clone());
        AssertCloneCopiesAll(new SessionExtras(), s => s.Clone());
        AssertCloneCopiesAll(new DefaultSettings(), d => d.Clone());
        AssertCloneCopiesAll(new UiPreferences(), u => u.Clone());
        AssertCloneCopiesAll(new ThemePalette(), t => t.Clone());
    }

    [Fact]
    public void ColorSettings_Clamp_limits_ranges_and_wraps_hue()
    {
        var c = new ColorSettings { Brightness = 2, Contrast = -1, Gamma = 99, Vibrance = 500, Hue = -30, ShadowLift = 1, Backend = (ColorBackend)42 };
        c.Clamp();
        Assert.Equal(1.0, c.Brightness);
        Assert.Equal(0.0, c.Contrast);
        Assert.Equal(6.0, c.Gamma);
        Assert.Equal(100, c.Vibrance);
        Assert.Equal(330, c.Hue);
        Assert.Equal(0.4, c.ShadowLift);
        Assert.Equal(ColorBackend.LowLevel, c.Backend);

        var d = new ColorSettings { Gamma = 99, Backend = ColorBackend.Driver };
        d.Clamp();
        Assert.Equal(2.8, d.Gamma);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertCloneCopiesAll<T>(T original, Func<T, T> clone) where T : class
    {
        var touched = FillWithNonDefaults(original, new HashSet<string>());
        Assert.Equal(typeof(T).GetProperties().Count(p => p.CanWrite && p.GetIndexParameters().Length == 0), touched.Count);
        var copy = clone(original);
        Assert.NotSame(original, copy);
        Assert.Equal(JsonSerializer.Serialize(original, Json), JsonSerializer.Serialize(copy, Json));
    }

    /// <summary>Sets every public settable property to a value that differs from its default; returns the names set.</summary>
    private static List<string> FillWithNonDefaults(object target, HashSet<string> exclude)
    {
        var touched = new List<string>();
        int seed = 7;
        foreach (var prop in target.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite || prop.GetIndexParameters().Length > 0 || exclude.Contains(prop.Name)) continue;
            var value = MakeValue(prop.PropertyType, prop.GetValue(target), ref seed);
            if (value == null) continue;
            prop.SetValue(target, value);
            touched.Add(prop.Name);
        }
        return touched;
    }

    private static object? MakeValue(Type type, object? current, ref int seed)
    {
        seed++;
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) return current as string == "val" + seed ? "other" + seed : "val" + seed;
        if (t == typeof(bool)) return !(current is true);
        if (t == typeof(int)) return (current is int i ? i : 0) + 11 + seed;
        if (t == typeof(uint)) return (current is uint u ? u : 0) + 11u;
        if (t == typeof(double)) return (current is double d ? d : 0) + 0.17 + seed / 100.0;
        if (t == typeof(float)) return (current is float f ? f : 0) + 0.17f;
        if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            foreach (var v in values)
                if (!Equals(v, current)) return v;
            return null;
        }
        if (t == typeof(ColorSettings))
            return new ColorSettings { Brightness = 0.31 + seed / 1000.0, Contrast = 1.21, Gamma = 1.33, Vibrance = 61 + seed, Hue = 12, ShadowLift = 0.12, Backend = ColorBackend.Driver, LockColor = !(current is ColorSettings cs && cs.LockColor) };
        if (t == typeof(SessionExtras))
            return new SessionExtras { DeferredApplySeconds = 3 + seed, AudioDeviceId = "dev" + seed, MonitorBrightness = 42, MonitorLayout = "isolatePrimary", ScalingMode = "stretch", QuietNotifications = true };
        if (t == typeof(ThemePalette))
            return new ThemePalette { Bg = "#00000" + seed % 10 };
        if (t == typeof(List<string>))
            return new List<string> { "s" + seed, "t" + seed };
        if (t == typeof(List<CompanionApp>))
            return new List<CompanionApp> { new() { Path = @"C:\c" + seed + ".exe", Arguments = "-a" } };
        if (t == typeof(List<QuickPreset>))
            return new List<QuickPreset> { new() { Name = "preset" + seed, Hotkey = "Ctrl+Alt+9", FpsLimit = 60 } };
        return null;
    }
}
