using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DisplayProfileManager;

public partial class ThemedDialog : Window
{
    public bool? Result { get; private set; }

    public ThemedDialog(string title, string message, bool showCancel = false)
    {
        InitializeComponent();
        TitleText.Text = string.IsNullOrWhiteSpace(title) ? "Display Profile Manager" : title;
        MessageText.Text = message ?? "";
        BtnCancel.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        if (showCancel)
        {
            BtnOk.Content = "Yes";
            BtnCancel.Content = "No";
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Fade + slight scale-in
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

    private void CloseWithFade(bool? result)
    {
        Result = result;
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

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseWithFade(false); e.Handled = true; }
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
}
