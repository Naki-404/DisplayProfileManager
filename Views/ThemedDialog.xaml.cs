using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public enum UnsavedChoice
{
    Stay,
    Save,
    Discard
}

public partial class ThemedDialog : Window
{
    public bool? Result { get; private set; }
    public UnsavedChoice UnsavedResult { get; private set; } = UnsavedChoice.Stay;

    private readonly bool _unsavedMode;

    public ThemedDialog(string title, string message, bool showCancel = false, bool unsavedMode = false)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Display Profile Manager" : title;
        MessageText.Text = message ?? "";
        _unsavedMode = unsavedMode;

        if (unsavedMode)
        {
            BtnDiscard.Visibility = Visibility.Visible;
            BtnCancel.Visibility = Visibility.Visible;
            BtnOk.Content = "Save";
            BtnCancel.Content = "Cancel";
            BtnDiscard.Content = "Don't save";
        }
        else
        {
            BtnDiscard.Visibility = Visibility.Collapsed;
            BtnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
            if (showCancel)
            {
                BtnOk.Content = "Yes";
                BtnCancel.Content = "No";
            }
        }
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
    }

    private void CloseWithFade(bool? result, UnsavedChoice unsaved = UnsavedChoice.Stay)
    {
        Result = result;
        UnsavedResult = unsaved;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            try { DialogResult = result == true; }
            catch { Close(); }
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
        => CloseWithFade(true, _unsavedMode ? UnsavedChoice.Save : UnsavedChoice.Stay);

    private void Cancel_Click(object sender, RoutedEventArgs e)
        => CloseWithFade(false, UnsavedChoice.Stay);

    private void Discard_Click(object sender, RoutedEventArgs e)
        => CloseWithFade(false, UnsavedChoice.Discard);

    private void Close_Click(object sender, RoutedEventArgs e)
        => CloseWithFade(false, UnsavedChoice.Stay);

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseWithFade(false, UnsavedChoice.Stay); e.Handled = true; }
        base.OnKeyDown(e);
    }

    public static bool Show(Window? owner, string message, string? title = null, bool confirm = false)
    {
        var dlg = new ThemedDialog(title ?? "Display Profile Manager", message, confirm)
        {
            Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        return dlg.ShowDialog() == true;
    }

    public static UnsavedChoice ShowUnsaved(Window? owner)
    {
        var dlg = new ThemedDialog(
            Loc.T("unsaved.title"),
            Loc.T("unsaved.message"),
            showCancel: true,
            unsavedMode: true)
        {
            Owner = owner,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen
        };
        dlg.BtnOk.Content = Loc.T("unsaved.save");
        dlg.BtnDiscard.Content = Loc.T("unsaved.discard");
        dlg.BtnCancel.Content = Loc.T("unsaved.cancel");
        dlg.ShowDialog();
        return dlg.UnsavedResult;
    }
}
