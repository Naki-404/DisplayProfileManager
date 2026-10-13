using System.Windows.Input;
using Visor.Models;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class HotkeyGestureTests
{
    private const uint ModAlt = 0x0001, ModCtrl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;

    [Theory]
    [InlineData("Ctrl+Alt+1", ModCtrl | ModAlt, 0x31u)]
    [InlineData("ctrl + alt + 1", ModCtrl | ModAlt, 0x31u)]
    [InlineData("Control+D1", ModCtrl, 0x31u)]
    [InlineData("Shift+F5", ModShift, 0x74u)]
    [InlineData("Win+Space", ModWin, 0x20u)]
    [InlineData("Ctrl+NumPad1", ModCtrl, 0x61u)]
    [InlineData("NumPad0", 0u, 0x60u)]
    [InlineData("Alt+A", ModAlt, 0x41u)]
    [InlineData("Alt+a", ModAlt, 0x41u)]
    [InlineData("Ctrl+`", ModCtrl, 0xC0u)]
    [InlineData("Ctrl+Add", ModCtrl, 0x6Bu)]
    [InlineData("Ctrl+Shift+Alt+F12", ModCtrl | ModShift | ModAlt, 0x7Bu)]
    [InlineData("XButton1", 0u, 0x1001u)]
    [InlineData("MouseXButton2", 0u, 0x1002u)]
    [InlineData("Ctrl+XButton2", ModCtrl, 0x1002u)]
    [InlineData("Ctrl+PageUp", ModCtrl, 0x21u)]
    public void TryParseGesture_parses(string gesture, uint mods, uint vk)
    {
        Assert.True(HotkeyService.TryParseGesture(gesture, out var m, out var v));
        Assert.Equal(mods, m);
        Assert.Equal(vk, v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl")]           // modifier only → "ctrl" is not a key
    [InlineData("Meta+A")]         // unknown modifier
    [InlineData("Ctrl+NotAKey")]
    [InlineData("Ctrl+Alt+NumPad")]
    [InlineData("LeftClick")]
    [InlineData("XButton3")]
    public void TryParseGesture_rejects(string gesture)
    {
        Assert.False(HotkeyService.TryParseGesture(gesture, out _, out _));
    }

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.D1, "Ctrl+Alt+1")]
    [InlineData(ModifierKeys.Shift, Key.F5, "Shift+F5")]
    [InlineData(ModifierKeys.Control, Key.NumPad1, "Ctrl+NumPad1")]
    [InlineData(ModifierKeys.Windows, Key.Space, "Win+Space")]
    [InlineData(ModifierKeys.Control, Key.OemTilde, "Ctrl+`")]
    [InlineData(ModifierKeys.Alt, Key.A, "Alt+A")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, Key.PageDown, "Ctrl+Shift+PageDown")]
    public void GestureFromKeys_formats(ModifierKeys mods, Key key, string expected)
    {
        Assert.Equal(expected, HotkeyService.GestureFromKeys(mods, key));
    }

    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.D1)]
    [InlineData(ModifierKeys.Shift, Key.F5)]
    [InlineData(ModifierKeys.Control, Key.NumPad7)]
    [InlineData(ModifierKeys.Windows, Key.Insert)]
    [InlineData(ModifierKeys.Control, Key.OemTilde)]
    [InlineData(ModifierKeys.Alt, Key.Z)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt, Key.Decimal)]
    [InlineData(ModifierKeys.Control, Key.Up)]
    public void Gesture_round_trips_through_format_and_parse(ModifierKeys mods, Key key)
    {
        var text = HotkeyService.GestureFromKeys(mods, key);
        Assert.True(HotkeyService.TryParseGesture(text, out var m, out var vk), text);
        Assert.Equal((uint)KeyInterop.VirtualKeyFromKey(key), vk);

        uint expectedMods = 0;
        if (mods.HasFlag(ModifierKeys.Control)) expectedMods |= ModCtrl;
        if (mods.HasFlag(ModifierKeys.Alt)) expectedMods |= ModAlt;
        if (mods.HasFlag(ModifierKeys.Shift)) expectedMods |= ModShift;
        if (mods.HasFlag(ModifierKeys.Windows)) expectedMods |= ModWin;
        Assert.Equal(expectedMods, m);
    }

    [Fact]
    public void GestureFromMouse_only_side_buttons_and_round_trips()
    {
        Assert.Equal("MouseXButton1", HotkeyService.GestureFromMouse(ModifierKeys.None, MouseButton.XButton1));
        Assert.Equal("Ctrl+MouseXButton2", HotkeyService.GestureFromMouse(ModifierKeys.Control, MouseButton.XButton2));
        Assert.Null(HotkeyService.GestureFromMouse(ModifierKeys.None, MouseButton.Left));
        Assert.Null(HotkeyService.GestureFromMouse(ModifierKeys.Control, MouseButton.Right));
        Assert.Null(HotkeyService.GestureFromMouse(ModifierKeys.None, MouseButton.Middle));

        Assert.True(HotkeyService.TryParseGesture("Ctrl+MouseXButton2", out var m, out var vk));
        Assert.Equal(ModCtrl, m);
        Assert.Equal(0x1002u, vk);
        Assert.True(HotkeyService.TryParseGesture("MouseXButton1", out _, out vk));
        Assert.Equal(0x1001u, vk);
    }

    // ------------------------------------------------------------------ conflicts

    private static AppConfig ConflictConfig() => new()
    {
        GlobalHotkeys = new GlobalHotkeys { ToggleOverlay = "Ctrl+Shift+O", EmergencyRestore = "Ctrl+Alt+NumPad0", ToggleZoom = "XButton1" },
        Profiles =
        {
            new GameProfile
            {
                Id = "g1", Name = "Game One",
                Presets = { new QuickPreset { Id = "p1", Name = "One", Hotkey = "Ctrl+Alt+1" }, new QuickPreset { Id = "p2", Name = "Two", Hotkey = "Ctrl+Alt+2" } }
            },
            new GameProfile
            {
                Id = "g2", Name = "Game Two",
                Presets = { new QuickPreset { Id = "p3", Name = "Three", Hotkey = "Ctrl+Alt+3" } }
            }
        }
    };

    [Fact]
    public void FindConflict_detects_global_hotkeys_case_insensitively()
    {
        var cfg = ConflictConfig();
        var msg = HotkeyService.FindConflictInConfig(cfg, "ctrl+shift+o");
        Assert.NotNull(msg);
        Assert.Contains("Toggle overlay", msg);
        Assert.Contains("Toggle zoom", HotkeyService.FindConflictInConfig(cfg, "MouseXButton1")!);
    }

    [Fact]
    public void FindConflict_detects_preset_hotkeys_across_all_profiles()
    {
        var cfg = ConflictConfig();
        var msg = HotkeyService.FindConflictInConfig(cfg, "Ctrl+Alt+3");
        Assert.NotNull(msg);
        Assert.Contains("Three", msg);
        Assert.Contains("Game Two", msg);
    }

    [Fact]
    public void FindConflict_scoped_to_one_profile_ignores_others()
    {
        var cfg = ConflictConfig();
        Assert.Null(HotkeyService.FindConflictInConfig(cfg, "Ctrl+Alt+3", scopeProfile: cfg.Profiles[0]));
        Assert.NotNull(HotkeyService.FindConflictInConfig(cfg, "Ctrl+Alt+1", scopeProfile: cfg.Profiles[0]));
    }

    [Fact]
    public void FindConflict_ignores_the_preset_being_edited()
    {
        var cfg = ConflictConfig();
        Assert.Null(HotkeyService.FindConflictInConfig(cfg, "Ctrl+Alt+1", ignorePresetId: "p1"));
        Assert.NotNull(HotkeyService.FindConflictInConfig(cfg, "Ctrl+Alt+1", ignorePresetId: "p2"));
    }

    [Fact]
    public void FindConflict_returns_null_for_free_empty_or_invalid_gestures()
    {
        var cfg = ConflictConfig();
        Assert.Null(HotkeyService.FindConflictInConfig(cfg, "Ctrl+Alt+9"));
        Assert.Null(HotkeyService.FindConflictInConfig(cfg, ""));
        Assert.Null(HotkeyService.FindConflictInConfig(cfg, "Bogus+Nope"));
    }
}
