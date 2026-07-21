using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using System.Windows.Interop;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Dictionary<int, string> _idToAction = new();
    private readonly List<string> _lastFailures = new();
    private IntPtr _hwnd;
    private int _nextId = 1;
    private HwndSourceHook? _hook;
    private HwndSource? _source;

    public event Action<string>? HotkeyPressed;

    /// <summary>Gestures that failed to register on the last RegisterFromConfig call.</summary>
    public IReadOnlyList<string> LastFailures => _lastFailures;

    public void Attach(IntPtr hwnd)
    {
        Detach();
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        if (_source == null) return;
        _hook = WndProc;
        _source.AddHook(_hook);
    }

    public void Detach()
    {
        UnregisterAll();
        if (_source != null && _hook != null)
            _source.RemoveHook(_hook);
        _source = null;
        _hook = null;
        _hwnd = IntPtr.Zero;
    }

    public void RegisterFromConfig(AppConfig config, GameProfile? activeProfile = null, GameProfile? fallbackProfile = null)
    {
        UnregisterAll();
        _lastFailures.Clear();
        if (_hwnd == IntPtr.Zero)
        {
            // MainWindow may reload config before SourceInitialized attaches HWND.
            return;
        }

        var hk = config.GlobalHotkeys ?? new GlobalHotkeys();
        TryRegister("brightnessUp", hk.BrightnessUp);
        TryRegister("brightnessDown", hk.BrightnessDown);
        TryRegister("contrastUp", hk.ContrastUp);
        TryRegister("contrastDown", hk.ContrastDown);
        TryRegister("gammaUp", hk.GammaUp);
        TryRegister("gammaDown", hk.GammaDown);
        TryRegister("resetColor", hk.ResetColor);
        TryRegister("compareAb", hk.CompareAb);
        TryRegister("shadowBoostUp", hk.ShadowBoostUp);
        TryRegister("shadowBoostDown", hk.ShadowBoostDown);
        TryRegister("nextPreset", hk.NextPreset);
        TryRegister("previousPreset", hk.PreviousPreset);
        TryRegister("toggleOverlay", hk.ToggleOverlay);
        TryRegister("emergencyRestore", hk.EmergencyRestore);

        // Prefer live game; otherwise the game selected on Presets / Profiles so hotkeys work before launch.
        var source = activeProfile ?? fallbackProfile;
        if (source != null)
            source = config.Profiles.FirstOrDefault(p => p.Id == source.Id) ?? source;

        foreach (var preset in source?.Presets ?? Enumerable.Empty<QuickPreset>())
        {
            if (!string.IsNullOrWhiteSpace(preset.Hotkey))
                TryRegister("preset:" + preset.Id, preset.Hotkey);
        }

        if (_lastFailures.Count > 0)
            AppLog.Error($"Hotkey register failed for {_lastFailures.Count} binding(s): {string.Join(", ", _lastFailures)}");
        else if (source != null)
            AppLog.Info($"Preset hotkeys scoped to: {source.Name} ({source.Presets?.Count(p => !string.IsNullOrWhiteSpace(p.Hotkey)) ?? 0} bound)");
    }

    public void TryRegister(string action, string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return;
        if (!TryParseGesture(gesture, out uint mods, out uint vk))
        {
            AppLog.Error($"Invalid hotkey '{gesture}' for {action}");
            return;
        }

        if (_hwnd == IntPtr.Zero)
        {
            AppLog.Error("HotkeyService not attached to HWND.");
            return;
        }

        // Reject bare keys (esp. NumPad) — they fire while typing / in-game and feel like "ghost presets".
        if (mods == 0)
        {
            AppLog.Info($"Skipped hotkey '{gesture}' for {action} — need Ctrl/Alt/Shift/Win.");
            _lastFailures.Add($"{action}={gesture} (need modifier)");
            return;
        }

        int id = _nextId++;
        if (!RegisterHotKey(_hwnd, id, mods, vk))
        {
            AppLog.Error($"Failed to register hotkey '{gesture}' for {action}");
            _lastFailures.Add($"{action}={gesture}");
            return;
        }

        _idToAction[id] = action;
        AppLog.Info($"Hotkey registered: {action} = {gesture}");
    }

    public void UnregisterAll()
    {
        foreach (var id in _idToAction.Keys.ToList())
        {
            if (_hwnd != IntPtr.Zero)
                UnregisterHotKey(_hwnd, id);
        }
        _idToAction.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_idToAction.TryGetValue(id, out var action))
            {
                HotkeyPressed?.Invoke(action);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public static bool TryParseGesture(string gesture, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;

        string keyPart = parts[^1];
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= 0x0002; break;
                case "alt": modifiers |= 0x0001; break;
                case "shift": modifiers |= 0x0004; break;
                case "win":
                case "windows": modifiers |= 0x0008; break;
                default: return false;
            }
        }

        virtualKey = keyPart.ToLowerInvariant() switch
        {
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            "add" or "numpadadd" or "+" => 0x6B,
            "subtract" or "numpadsubtract" or "-" => 0x6D,
            "numpad0" => 0x60,
            "numpad1" => 0x61,
            "numpad2" => 0x62,
            "numpad3" => 0x63,
            "numpad4" => 0x64,
            "numpad5" => 0x65,
            "numpad6" => 0x66,
            "numpad7" => 0x67,
            "numpad8" => 0x68,
            "numpad9" => 0x69,
            "d0" or "0" => 0x30,
            "d1" or "1" => 0x31,
            "d2" or "2" => 0x32,
            "d3" or "3" => 0x33,
            "d4" or "4" => 0x34,
            "d5" or "5" => 0x35,
            "d6" or "6" => 0x36,
            "d7" or "7" => 0x37,
            "d8" or "8" => 0x38,
            "d9" or "9" => 0x39,
            "f1" => 0x70,
            "f2" => 0x71,
            "f3" => 0x72,
            "f4" => 0x73,
            "f5" => 0x74,
            "f6" => 0x75,
            "f7" => 0x76,
            "f8" => 0x77,
            "f9" => 0x78,
            "f10" => 0x79,
            "f11" => 0x7A,
            "f12" => 0x7B,
            _ when keyPart.Length == 1 => (uint)char.ToUpperInvariant(keyPart[0]),
            _ => 0
        };

        return virtualKey != 0;
    }

    public static string GestureFromKeys(ModifierKeys mods, Key key)
    {
        var sb = new StringBuilder();
        if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");

        string k = key switch
        {
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Add => "Add",
            Key.Subtract => "Subtract",
            Key.NumPad0 => "NumPad0",
            Key.NumPad1 => "NumPad1",
            Key.NumPad2 => "NumPad2",
            Key.NumPad3 => "NumPad3",
            Key.NumPad4 => "NumPad4",
            Key.NumPad5 => "NumPad5",
            Key.NumPad6 => "NumPad6",
            Key.NumPad7 => "NumPad7",
            Key.NumPad8 => "NumPad8",
            Key.NumPad9 => "NumPad9",
            Key.D0 => "0",
            Key.D1 => "1",
            Key.D2 => "2",
            Key.D3 => "3",
            Key.D4 => "4",
            Key.D5 => "5",
            Key.D6 => "6",
            Key.D7 => "7",
            Key.D8 => "8",
            Key.D9 => "9",
            _ => key.ToString()
        };
        sb.Append(k);
        return sb.ToString();
    }

    public void Dispose() => Detach();
}
