using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Visor.Services;

namespace Visor;

/// <summary>
/// Themed replacement for MessageBox: info, confirm (optionally destructive) and text prompt.
/// Enter = primary button, Esc / × = cancel.
/// </summary>
public partial class ThemedDialog : Window
{
    private bool _closing;

    public ThemedDialog(string title, string message, bool showCancel = false)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? Loc.T("app.name") : title;
        Title = TitleText.Text;
        MessageText.Text = message ?? "";
        BtnClose.ToolTip = Loc.T("btn.close");
        BtnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        BtnOk.Content = showCancel ? Loc.T("btn.yes") : Loc.T("btn.ok");
        BtnCancel.Content = showCancel ? Loc.T("btn.no") : Loc.T("btn.cancel");
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Card.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
        Card.RenderTransform = new System.Windows.Media.ScaleTransform(0.96, 0.96);

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(OpacityProperty, fade);

        var scaleX = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Card.RenderTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, scaleX);
        Card.RenderTransform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, scaleY);

        if (InputBox.Visibility == Visibility.Visible)
        {
            InputBox.Focus();
            InputBox.SelectAll();
        }
        else if (BtnCancel.Visibility == Visibility.Visible && ReferenceEquals(BtnOk.Style, TryFindResource("DangerButton")))
            BtnCancel.Focus(); // destructive: safe default for keyboard users (Enter still = primary)
        else
            BtnOk.Focus();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); } catch { }
    }

    private void CloseWithFade(bool result)
    {
        if (_closing) return;
        _closing = true;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            try { DialogResult = result; }
            catch { Close(); }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => CloseWithFade(true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => CloseWithFade(false);

    private void Close_Click(object sender, RoutedEventArgs e) => CloseWithFade(false);

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseWithFade(false); e.Handled = true; }
        base.OnPreviewKeyDown(e);
    }

    private static ThemedDialog Create(Window? owner, string message, string? title, bool showCancel)
    {
        var dlg = new ThemedDialog(title ?? Loc.T("app.name"), message, showCancel);
        if (owner != null && owner.IsVisible)
        {
            dlg.Owner = owner;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
            dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dlg;
    }

    /// <summary>Info (OK) or yes/no confirm.</summary>
    public static bool Show(Window? owner, string message, string? title = null, bool confirm = false)
    {
        var dlg = Create(owner, message, title, confirm);
        return dlg.ShowDialog() == true;
    }

    /// <summary>Confirm with an explicit action label ("Delete", "Import"…). <paramref name="danger"/> paints it red.</summary>
    public static bool Confirm(Window? owner, string message, string title, string confirmText, bool danger = false)
    {
        var dlg = Create(owner, message, title, showCancel: true);
        dlg.BtnOk.Content = confirmText;
        dlg.BtnCancel.Content = Loc.T("btn.cancel");
        if (danger && dlg.TryFindResource("DangerButton") is Style st)
            dlg.BtnOk.Style = st;
        return dlg.ShowDialog() == true;
    }

    /// <summary>Single-line text prompt. Returns null when cancelled.</summary>
    public static string? Prompt(Window? owner, string title, string message, string defaultText, string? okText = null)
    {
        var dlg = Create(owner, message, title, showCancel: true);
        dlg.InputBox.Visibility = Visibility.Visible;
        dlg.InputBox.Text = defaultText ?? "";
        dlg.BtnOk.Content = okText ?? Loc.T("btn.ok");
        dlg.BtnCancel.Content = Loc.T("btn.cancel");
        if (string.IsNullOrWhiteSpace(message))
            dlg.MessageText.Visibility = Visibility.Collapsed;
        return dlg.ShowDialog() == true ? dlg.InputBox.Text : null;
    }
}
