using System.Windows.Input;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace Visor.Services;

/// <summary>What a key press inside a hotkey capture box means.</summary>
public enum HotkeyCaptureKind
{
    /// <summary>Let the key through (Tab navigation) or ignore it (lone modifier).</summary>
    Ignore,
    /// <summary>Esc — restore the previous binding and drop focus.</summary>
    Cancel,
    /// <summary>Backspace / Delete — clear the binding.</summary>
    Clear,
    /// <summary>A complete gesture was captured (<see cref="HotkeyCapture.Read"/> returns it).</summary>
    Gesture
}

/// <summary>
/// Keyboard / mouse-button capture shared by the preset hotkey box (Games) and the app hotkey boxes
/// (Settings). Conflict checks and persistence stay with the page that owns the box.
/// </summary>
public static class HotkeyCapture
{
    /// <summary>Focus entered a capture box: announce the listening state and remember the old text.</summary>
    public static void OnGotFocus(WpfTextBox box, IDictionary<WpfTextBox, string> original)
    {
        original[box] = box.Text;
        box.Tag = Loc.T("hotkey.listening");
        // Announce the capture state to assistive tech (the watermark alone is visual only).
        System.Windows.Automation.AutomationProperties.SetLiveSetting(box, System.Windows.Automation.AutomationLiveSetting.Polite);
        System.Windows.Automation.AutomationProperties.SetItemStatus(box, Loc.T("hotkey.listening"));
        System.Windows.Automation.AutomationProperties.SetHelpText(box, Loc.T("hotkey.listening"));
        box.SelectAll();
    }

    public static void OnLostFocus(WpfTextBox box)
    {
        box.Tag = Loc.T("hotkey.placeholder");
        System.Windows.Automation.AutomationProperties.SetItemStatus(box, "");
        System.Windows.Automation.AutomationProperties.SetHelpText(box, Loc.T("hotkey.placeholder"));
    }

    /// <summary>
    /// Classifies a key press. Sets <c>e.Handled</c> for everything except Tab navigation.
    /// </summary>
    public static HotkeyCaptureKind Read(System.Windows.Input.KeyEventArgs e, out string gesture)
    {
        gesture = "";
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        // Tab / Shift+Tab keep normal keyboard navigation.
        if (key == Key.Tab && (mods & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
            return HotkeyCaptureKind.Ignore;

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return HotkeyCaptureKind.Ignore;

        if (key == Key.Escape && mods == ModifierKeys.None)
            return HotkeyCaptureKind.Cancel;

        if ((key is Key.Back or Key.Delete) && mods == ModifierKeys.None)
            return HotkeyCaptureKind.Clear;

        gesture = HotkeyService.GestureFromKeys(mods, key);
        return string.IsNullOrWhiteSpace(gesture) ? HotkeyCaptureKind.Ignore : HotkeyCaptureKind.Gesture;
    }

    /// <summary>
    /// Mouse XButton1/XButton2 as hotkeys on the same boxes as keyboard capture (see HotkeyService's
    /// WH_MOUSE_LL fallback). Returns null for any other button.
    /// </summary>
    public static string? ReadMouse(MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.XButton1 or MouseButton.XButton2))
            return null;
        return HotkeyService.GestureFromMouse(Keyboard.Modifiers, e.ChangedButton);
    }

    public static bool SameGesture(string? a, uint mods, uint vk) =>
        !string.IsNullOrWhiteSpace(a)
        && HotkeyService.TryParseGesture(a!, out var am, out var av)
        && am == mods && av == vk;

    public static string? Normalize(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
