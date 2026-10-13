using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Visor.Models;

namespace Visor.Services;

/// <summary>
/// Global hotkeys. Each binding is delivered by exactly one path: RegisterHotKey (WM_HOTKEY) when
/// Windows grants it, otherwise the low-level keyboard / mouse hook. Hook callbacks only match and
/// enqueue (Dispatcher.BeginInvoke) — they never run app work, so Windows never times the hook out.
/// <see cref="HotkeyPressed"/> is raised on the UI thread.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmKeyup = 0x0101;
    private const int WmSyskeydown = 0x0104;
    private const int WmSyskeyup = 0x0105;
    private const uint LlkhfUp = 0x80;

    private const uint ModAlt = 0x0001;
    private const uint ModCtrl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private const int WhMouseLl = 14;
    private const int WmXButtonDown = 0x020B;
    private const uint XButton1Flag = 0x0001;
    private const uint XButton2Flag = 0x0002;

    /// <summary>Same action again within this window (measured on event arrival) is a duplicate.</summary>
    private const long DebounceMs = 220;
    /// <summary>Hook event and WM_HOTKEY for the same press carry (nearly) the same input time.</summary>
    private const long CrossPathWindowMs = 250;

    /// <summary>
    /// Pseudo virtual-key codes for mouse side buttons — outside the real VK range (0x01..0xFE),
    /// so they can share the same binding table/mods logic as keyboard VKs without colliding.
    /// Mouse buttons cannot be registered with RegisterHotKey; they only work via the LL hook.
    /// </summary>
    private const uint VkMouseXButton1 = 0x1001;
    private const uint VkMouseXButton2 = 0x1002;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointL
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public PointL Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    /// <summary>One binding; <see cref="Registered"/> = RegisterHotKey also delivers it (WM_HOTKEY).</summary>
    private sealed record Binding(uint Mods, uint Vk, string Action, bool Registered);

    /// <summary>Last action actually delivered per path, with its input event time (GetTickCount clock).</summary>
    private (string Action, uint Time)? _lastFromHook;
    private (string Action, uint Time)? _lastFromHotkeyMsg;

    [DllImport("user32.dll")]
    private static extern int GetMessageTime();

    private readonly Dictionary<int, string> _idToAction = new();
    /// <summary>Replaced as a whole (copy-on-write) so hook callbacks read it without locking.</summary>
    private volatile Binding[] _bindings = Array.Empty<Binding>();
    private readonly List<string> _lastFailures = new();
    /// <summary>Hook-delivered keys currently held down (auto-repeat suppression). Hook thread only.</summary>
    private readonly HashSet<uint> _heldVks = new();
    private IntPtr _hwnd;
    private int _nextId = 1;
    private HwndSourceHook? _hook;
    private HwndSource? _source;
    private Dispatcher? _dispatcher;
    private Thread? _hookThread;
    private Dispatcher? _hookDispatcher;
    private readonly object _debounceGate = new();
    private IntPtr _llHook = IntPtr.Zero;
    private LowLevelProc? _llProc;
    private IntPtr _mouseHook = IntPtr.Zero;
    private LowLevelProc? _mouseProc;
    private volatile bool _uiTextInputFocused;
    private bool _focusTrackingHooked;

    /// <summary>Raised on the UI thread, once per physical press.</summary>
    public event Action<string>? HotkeyPressed;

    /// <summary>Gestures that failed to register on the last RegisterFromConfig call.</summary>
    public IReadOnlyList<string> LastFailures => _lastFailures;

    public void Attach(IntPtr hwnd)
    {
        Detach();
        _hwnd = hwnd;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _source = HwndSource.FromHwnd(hwnd);
        if (_source == null) return;
        _hook = WndProc;
        _source.AddHook(_hook);
        StartHookThread();
        HookUiFocusTracking();
    }

    private void HookUiFocusTracking()
    {
        // Class handlers are process-wide and cannot be removed — register once.
        if (_focusTrackingHooked) return;
        _focusTrackingHooked = true;
        try
        {
            foreach (var type in new[]
                     {
                         typeof(System.Windows.Controls.TextBox),
                         typeof(System.Windows.Controls.PasswordBox),
                         typeof(System.Windows.Controls.ComboBox)
                     })
            {
                EventManager.RegisterClassHandler(type, UIElement.GotKeyboardFocusEvent,
                    new KeyboardFocusChangedEventHandler((_, _) => _uiTextInputFocused = true));
                EventManager.RegisterClassHandler(type, UIElement.LostKeyboardFocusEvent,
                    new KeyboardFocusChangedEventHandler((_, _) => _uiTextInputFocused = false));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Hotkey focus tracking: " + ex.Message);
        }
    }

    public void Detach()
    {
        UnregisterAll();
        StopHookThread();
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
        TryRegister("toggleZoom", hk.ToggleZoom);

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
                     ("toggleZoom", hk.ToggleZoom),
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
        "toggleZoom" => "Toggle zoom",
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

        // The same gesture twice (e.g. an imported preset): RegisterHotKey would refuse the second
        // one and a single press would fire both actions. Keep the first binding, report the rest.
        var clash = _bindings.FirstOrDefault(b => b.Mods == mods && b.Vk == vk);
        if (clash != null)
        {
            AppLog.Warn($"Hotkey '{gesture}' for {action} conflicts with {DescribeAction(clash.Action)} — not registered.");
            _lastFailures.Add($"{gesture} ({DescribeAction(action)} / {DescribeAction(clash.Action)})");
            return;
        }

        bool registered = false;
        if (vk is VkMouseXButton1 or VkMouseXButton2)
        {
            // Mouse side buttons have no RegisterHotKey equivalent — hook-only.
            AppLog.Info($"Hotkey registered (mouse hook): {action} = {gesture}");
        }
        else
        {
            int id = _nextId++;
            if (RegisterHotKey(_hwnd, id, mods | ModNoRepeat, vk))
            {
                _idToAction[id] = action;
                registered = true;
                AppLog.Info($"Hotkey registered: {action} = {gesture}");
            }
            else
            {
                // Another app owns the combo — the LL hook still sees it (exclusive fullscreen too).
                AppLog.Info($"RegisterHotKey busy for '{gesture}' ({action}) — keyboard hook only.");
            }
        }

        // The LL hook matches every binding (games using raw input with RIDEV_NOHOTKEYS swallow
        // WM_HOTKEY); a press seen by both paths is de-duplicated by input event time in Enqueue.
        _bindings = _bindings.Append(new Binding(mods, vk, action, registered)).ToArray();
    }


    public void UnregisterAll()
    {
        foreach (var id in _idToAction.Keys.ToList())
        {
            if (_hwnd != IntPtr.Zero)
                UnregisterHotKey(_hwnd, id);
        }
        _idToAction.Clear();
        _bindings = Array.Empty<Binding>();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int id = wParam.ToInt32();
            if (_idToAction.TryGetValue(id, out var action))
            {
                Enqueue(action, unchecked((uint)GetMessageTime()), fromHook: false);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// LL hooks run on the thread that installed them; a dedicated message-pumping thread keeps
    /// them responsive even while the UI thread is busy (mode change, dialog), so Windows never
    /// drops the hook for timing out.
    /// </summary>
    private void StartHookThread()
    {
        if (_hookThread != null) return;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            _hookDispatcher = Dispatcher.CurrentDispatcher;
            InstallLlHook();
            InstallMouseHook();
            try { ready.Set(); }
            catch (ObjectDisposedException) { /* caller stopped waiting */ }
            Dispatcher.Run();
            RemoveLlHook();
            RemoveMouseHook();
        })
        {
            IsBackground = true,
            Name = "Visor hotkey hook"
        };
        _hookThread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(3)))
            AppLog.Error("Hotkey hook thread did not start in time.");
    }

    private void StopHookThread()
    {
        var dispatcher = _hookDispatcher;
        var thread = _hookThread;
        _hookDispatcher = null;
        _hookThread = null;
        if (dispatcher == null || thread == null) return;
        try
        {
            dispatcher.InvokeShutdown();
            if (!thread.Join(TimeSpan.FromSeconds(2)))
                AppLog.Warn("Hotkey hook thread did not stop in time.");
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Hotkey hook thread stop");
        }
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

    /// <summary>
    /// WH_MOUSE_LL fallback so mouse XButton1/XButton2 hotkeys keep working in exclusive
    /// fullscreen games, mirroring the keyboard LL hook above.
    /// </summary>
    private void InstallMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseProc = MouseLlCallback;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
        if (_mouseHook == IntPtr.Zero)
            AppLog.Error("Low-level mouse hook failed — mouse-button hotkeys unavailable.");
        else
            AppLog.Info("Low-level mouse hook installed for hotkeys.");
    }

    private void RemoveMouseHook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _mouseProc = null;
    }

    /// <summary>Hook callback: match + enqueue only. Must return fast.</summary>
    private IntPtr MouseLlCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && wParam == (IntPtr)WmXButtonDown && !_uiTextInputFocused)
            {
                var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                uint vk = ((info.MouseData >> 16) & 0xFFFF) switch
                {
                    XButton1Flag => VkMouseXButton1,
                    XButton2Flag => VkMouseXButton2,
                    _ => 0
                };
                if (vk != 0 && MatchHookBinding(vk, ReadModifiers(0)) is { } action)
                    Enqueue(action, info.Time, fromHook: true);
            }
        }
        catch { /* never break the hook chain */ }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>Hook callback: match + enqueue only. Must return fast.</summary>
    private IntPtr LlCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                if (msg is WmKeyup or WmSyskeyup || (info.Flags & LlkhfUp) != 0)
                {
                    _heldVks.Remove(info.VkCode);
                }
                else if (msg is WmKeydown or WmSyskeydown && !_uiTextInputFocused)
                {
                    // Don't steal keys while typing in our own fields (flag set on UI thread).
                    if (MatchHookBinding(info.VkCode, ReadModifiers(info.VkCode)) is { } action
                        && _heldVks.Add(info.VkCode)) // auto-repeat while held → ignored
                        Enqueue(action, info.Time, fromHook: true);
                }
            }
        }
        catch { /* never break the hook chain */ }

        return CallNextHookEx(_llHook, nCode, wParam, lParam);
    }

    private string? MatchHookBinding(uint vk, uint mods)
    {
        foreach (var b in _bindings)
        {
            if (b.Vk == vk && b.Mods == mods)
                return b.Action;
        }
        return null;
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

    /// <summary>
    /// De-duplicates by input event time (not arrival time — the UI thread may be busy), then hands
    /// the action to the UI thread asynchronously. One physical press seen by both the LL hook and
    /// WM_HOTKEY (event times within <see cref="CrossPathWindowMs"/>) is delivered once.
    /// </summary>
    private void Enqueue(string action, uint eventTime, bool fromHook)
    {
        lock (_debounceGate)
        {
            var other = fromHook ? _lastFromHotkeyMsg : _lastFromHook;
            if (other is { } o && o.Action == action && TickDistance(o.Time, eventTime) <= CrossPathWindowMs)
                return;
            var same = fromHook ? _lastFromHook : _lastFromHotkeyMsg;
            if (same is { } s && s.Action == action && TickDistance(s.Time, eventTime) < DebounceMs)
                return;
            if (fromHook) _lastFromHook = (action, eventTime);
            else _lastFromHotkeyMsg = (action, eventTime);
        }

        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            try { HotkeyPressed?.Invoke(action); }
            catch (Exception ex) { AppLog.Error("Hotkey handler: " + ex.Message); }
        });
    }

    /// <summary>|a − b| on the 32-bit GetTickCount clock (wrap-safe).</summary>
    private static uint TickDistance(uint a, uint b)
    {
        int d = unchecked((int)(a - b));
        return (uint)Math.Abs((long)d);
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
            "mousexbutton1" or "xbutton1" => VkMouseXButton1,
            "mousexbutton2" or "xbutton2" => VkMouseXButton2,
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

    /// <summary>
    /// Builds a gesture string for mouse side buttons (XButton1/XButton2) — the only mouse
    /// buttons Windows exposes as distinct, non-primary-click input. Returns null for any
    /// other button (left/right/middle/wheel are reserved for normal UI/game interaction).
    /// </summary>
    public static string? GestureFromMouse(ModifierKeys mods, MouseButton button)
    {
        string? name = button switch
        {
            MouseButton.XButton1 => "MouseXButton1",
            MouseButton.XButton2 => "MouseXButton2",
            _ => null
        };
        if (name == null) return null;

        var sb = new StringBuilder();
        if (mods.HasFlag(ModifierKeys.Control)) sb.Append("Ctrl+");
        if (mods.HasFlag(ModifierKeys.Alt)) sb.Append("Alt+");
        if (mods.HasFlag(ModifierKeys.Shift)) sb.Append("Shift+");
        if (mods.HasFlag(ModifierKeys.Windows)) sb.Append("Win+");
        sb.Append(name);
        return sb.ToString();
    }

    public void Dispose() => Detach();
}
