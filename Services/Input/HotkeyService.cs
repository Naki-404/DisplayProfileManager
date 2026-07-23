using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using DisplayProfileManager.Models;

namespace DisplayProfileManager.Services;

public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;

    private const uint ModAlt = 0x0001;
    private const uint ModCtrl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    private readonly Dictionary<int, string> _idToAction = new();
    private readonly List<(uint mods, uint vk, string action)> _bindings = new();
    private readonly object _bindGate = new();
    private readonly List<string> _lastFailures = new();
    private IntPtr _hwnd;
    private int _nextId = 1;
    private HwndSourceHook? _hook;
    private HwndSource? _source;
    private IntPtr _llHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _llProc;
    private string? _lastAction;
    private long _lastActionTicks;
    private volatile bool _uiTextInputFocused;

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
        InstallLlHook();
        HookUiFocusTracking();
    }

    /// <summary>UI-thread flag so the LL hook never touches FocusedElement off-thread.</summary>
    public void SetUiTextInputFocused(bool focused) => _uiTextInputFocused = focused;

    private void HookUiFocusTracking()
    {
        try
        {
            EventManager.RegisterClassHandler(
                typeof(System.Windows.Controls.TextBox),
                UIElement.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((_, _) => _uiTextInputFocused = true));
            EventManager.RegisterClassHandler(
                typeof(System.Windows.Controls.TextBox),
                UIElement.LostKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((_, _) => _uiTextInputFocused = false));
            EventManager.RegisterClassHandler(
                typeof(System.Windows.Controls.PasswordBox),
                UIElement.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((_, _) => _uiTextInputFocused = true));
            EventManager.RegisterClassHandler(
                typeof(System.Windows.Controls.PasswordBox),
                UIElement.LostKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((_, _) => _uiTextInputFocused = false));
            EventManager.RegisterClassHandler(
                typeof(System.Windows.Controls.ComboBox),
                UIElement.GotKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((_, _) => _uiTextInputFocused = true));
            EventManager.RegisterClassHandler(
                typeof(System.Windows.Controls.ComboBox),
                UIElement.LostKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((_, _) => _uiTextInputFocused = false));
        }
        catch (Exception ex)
        {
            AppLog.Error("Hotkey focus tracking: " + ex.Message);
        }
    }

    public void Detach()
    {
        UnregisterAll();
        RemoveLlHook();
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
        TryRegister("toggleOverlay", hk.ToggleOverlay);
        TryRegister("emergencyRestore", hk.EmergencyRestore);
        TryRegister("nextPreset", hk.NextPreset);
        TryRegister("previousPreset", hk.PreviousPreset);
        TryRegister("compareAb", hk.CompareAb);

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

    /// <summary>Check gesture against currently registered bindings. Returns a short conflict label or null.</summary>
    public string? FindConflict(string gesture, string? ignoreAction = null)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return null;
        if (!TryParseGesture(gesture, out uint mods, out uint vk)) return null;

        lock (_bindGate)
        {
            foreach (var b in _bindings)
            {
                if (b.mods != mods || b.vk != vk) continue;
                if (!string.IsNullOrWhiteSpace(ignoreAction)
                    && string.Equals(b.action, ignoreAction, StringComparison.OrdinalIgnoreCase))
                    continue;
                return $"Conflicts with {DescribeAction(b.action)}";
            }
        }
        return null;
    }

    /// <summary>
    /// Scan config globals + preset hotkeys for a gesture conflict.
    /// <paramref name="ignorePresetId"/> skips that preset (editing self).
    /// <paramref name="scopeProfile"/> limits preset scan to one game when set; otherwise all profiles.
    /// </summary>
    public static string? FindConflictInConfig(
        AppConfig cfg, string gesture, string? ignorePresetId = null, GameProfile? scopeProfile = null)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return null;
        if (!TryParseGesture(gesture, out uint mods, out uint vk)) return null;

        var hk = cfg.GlobalHotkeys ?? new GlobalHotkeys();
        foreach (var (action, g) in new (string, string?)[]
                 {
                     ("toggleOverlay", hk.ToggleOverlay),
                     ("emergencyRestore", hk.EmergencyRestore),
                     ("nextPreset", hk.NextPreset),
                     ("previousPreset", hk.PreviousPreset),
                     ("compareAb", hk.CompareAb),
                 })
        {
            if (string.IsNullOrWhiteSpace(g)) continue;
            if (!TryParseGesture(g, out uint gm, out uint gv)) continue;
            if (gm == mods && gv == vk)
                return $"Conflicts with global '{DescribeAction(action)}' ({g})";
        }

        IEnumerable<GameProfile> profiles = scopeProfile != null
            ? new[] { cfg.Profiles.FirstOrDefault(p => p.Id == scopeProfile.Id) ?? scopeProfile }
            : cfg.Profiles;

        foreach (var profile in profiles)
        {
            if (profile?.Presets == null) continue;
            foreach (var preset in profile.Presets)
            {
                if (string.IsNullOrWhiteSpace(preset.Hotkey)) continue;
                if (!string.IsNullOrWhiteSpace(ignorePresetId)
                    && string.Equals(preset.Id, ignorePresetId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!TryParseGesture(preset.Hotkey, out uint pm, out uint pv)) continue;
                if (pm == mods && pv == vk)
                    return $"Conflicts with preset '{preset.Name}' on {profile.Name} ({preset.Hotkey})";
            }
        }

        return null;
    }

    private static string DescribeAction(string action) => action switch
    {
        "toggleOverlay" => "Toggle overlay",
        "emergencyRestore" => "Emergency restore",
        "nextPreset" => "Next preset",
        "previousPreset" => "Previous preset",
        "compareAb" => "A/B compare",
        _ when action.StartsWith("preset:", StringComparison.OrdinalIgnoreCase)
            => "Preset " + action["preset:".Length..],
        _ => action
    };

    public void TryRegister(string action, string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return;
        if (!TryParseGesture(gesture, out uint mods, out uint vk))
        {
            AppLog.Error($"Invalid hotkey '{gesture}' for {action}");
            _lastFailures.Add($"{action}={gesture} (invalid)");
            return;
        }

        if (_hwnd == IntPtr.Zero)
        {
            AppLog.Error("HotkeyService not attached to HWND.");
            return;
        }

        // Always track for LL hook (works in exclusive fullscreen where RegisterHotKey often fails).
        // NumPad is registered as NumPad VK only — no Insert/arrow aliases (those stole unrelated keys).
        lock (_bindGate)
            _bindings.Add((mods, vk, action));

        int id = _nextId++;
        if (RegisterHotKey(_hwnd, id, mods | ModNoRepeat, vk))
        {
            _idToAction[id] = action;
            AppLog.Info($"Hotkey registered: {action} = {gesture}");
        }
        else
        {
            // LL hook can still deliver; don't treat as hard failure.
            AppLog.Info($"RegisterHotKey busy for '{gesture}' ({action}) — using keyboard hook fallback.");
        }
    }

    public void UnregisterAll()
    {
        foreach (var id in _idToAction.Keys.ToList())
        {
            if (_hwnd != IntPtr.Zero)
                UnregisterHotKey(_hwnd, id);
        }
        _idToAction.Clear();
        lock (_bindGate) _bindings.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_idToAction.TryGetValue(id, out var action))
            {
                Raise(action);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void InstallLlHook()
    {
        if (_llHook != IntPtr.Zero) return;
        _llProc = LlCallback;
        _llHook = SetWindowsHookEx(WhKeyboardLl, _llProc, GetModuleHandle(null), 0);
        if (_llHook == IntPtr.Zero)
            AppLog.Error("Low-level keyboard hook failed — in-game hotkeys may be unreliable.");
        else
            AppLog.Info("Low-level keyboard hook installed for hotkeys.");
    }

    private void RemoveLlHook()
    {
        if (_llHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_llHook);
            _llHook = IntPtr.Zero;
        }
        _llProc = null;
    }

    private IntPtr LlCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && (wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown))
            {
                // Don't steal keys while typing in our own fields (flag set on UI thread).
                if (_uiTextInputFocused)
                    return CallNextHookEx(_llHook, nCode, wParam, lParam);

                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if ((info.Flags & 0x80) == 0)
                {
                    uint mods = ReadModifiers(info.VkCode);
                    string? action = null;
                    lock (_bindGate)
                    {
                        foreach (var b in _bindings)
                        {
                            if (b.vk == info.VkCode && b.mods == mods)
                            {
                                action = b.action;
                                break;
                            }
                        }
                    }
                    if (action != null)
                        Raise(action);
                }
            }
        }
        catch { /* never break the hook chain */ }

        return CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    private static uint ReadModifiers(uint pressedVk)
    {
        // Don't count the key itself as a modifier when it is Ctrl/Alt/Shift/Win
        uint mods = 0;
        if (pressedVk is not (0xA0 or 0xA1 or 0x10) && (GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= ModShift; // VK_SHIFT
        if (pressedVk is not (0xA2 or 0xA3 or 0x11) && (GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= ModCtrl;
        if (pressedVk is not (0xA4 or 0xA5 or 0x12) && (GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= ModAlt;
        if (pressedVk is not (0x5B or 0x5C) && ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0))
            mods |= ModWin;
        return mods;
    }

    private void Raise(string action)
    {
        long now = Environment.TickCount64;
        if (action == _lastAction && now - _lastActionTicks < 220)
            return;
        _lastAction = action;
        _lastActionTicks = now;
        try { HotkeyPressed?.Invoke(action); }
        catch (Exception ex) { AppLog.Error("Hotkey handler: " + ex.Message); }
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
                case "control": modifiers |= ModCtrl; break;
                case "alt": modifiers |= ModAlt; break;
                case "shift": modifiers |= ModShift; break;
                case "win":
                case "windows": modifiers |= ModWin; break;
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
            "multiply" or "numpadmultiply" or "*" => 0x6A,
            "divide" or "numpaddivide" or "/" => 0x6F,
            "decimal" or "numpaddecimal" => 0x6E,
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
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" or "pgup" => 0x21,
            "pagedown" or "pgdn" => 0x22,
            "space" => 0x20,
            "tab" => 0x09,
            "oemtilde" or "`" => 0xC0,
            _ when keyPart.Length == 1 => (uint)char.ToUpperInvariant(keyPart[0]),
            _ when keyPart.Length > 1 && Enum.TryParse<Key>(keyPart, true, out var k)
                => (uint)KeyInterop.VirtualKeyFromKey(k),
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

        // Normalize SystemKey / ImeProcessed
        if (key == Key.System) key = Key.None;

        string k = key switch
        {
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Add => "Add",
            Key.Subtract => "Subtract",
            Key.Multiply => "Multiply",
            Key.Divide => "Divide",
            Key.Decimal => "Decimal",
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
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.OemTilde => "`",
            _ => key.ToString()
        };
        sb.Append(k);
        return sb.ToString();
    }

    public void Dispose() => Detach();
}
