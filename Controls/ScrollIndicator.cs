using System.Windows;
using System.Windows.Controls;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using System.Windows.Threading;

namespace Visor.Controls;

/// <summary>
/// Auto-hiding overlay scrollbars (set by the implicit ScrollViewer style in App.xaml). The template draws the bars
/// on top of the content with Opacity 0; this behavior fades them in when the user actually scrolls or moves the
/// pointer over the scroll area, and fades them out <see cref="HoldMs"/> later unless the pointer is on a bar or
/// a thumb is being dragged. Content-size changes (1 Hz sensor text, window resize) deliberately do NOT reveal the
/// bars, so live pages don't flash a scrollbar every second. Bars stay Collapsed when the content fits.
/// </summary>
public static class ScrollIndicator
{
    /// <summary>How long the bar stays visible after the last scroll / pointer movement.</summary>
    public const int HoldMs = 1200;

    public static readonly DependencyProperty AutoHideProperty = DependencyProperty.RegisterAttached(
        "AutoHide", typeof(bool), typeof(ScrollIndicator), new PropertyMetadata(false, OnAutoHideChanged));

    public static bool GetAutoHide(DependencyObject d) => (bool)d.GetValue(AutoHideProperty);
    public static void SetAutoHide(DependencyObject d, bool value) => d.SetValue(AutoHideProperty, value);

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(State), typeof(ScrollIndicator), new PropertyMetadata(null));

    private static void OnAutoHideChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        var state = sv.GetValue(StateProperty) as State;
        if (e.NewValue is true)
        {
            if (state != null) return;
            state = new State(sv);
            sv.SetValue(StateProperty, state);
            state.Attach();
        }
        else if (state != null)
        {
            state.Detach();
            sv.ClearValue(StateProperty);
        }
    }

    private sealed class State
    {
        private readonly ScrollViewer _sv;
        private DispatcherTimer? _timer;
        private bool _shown;

        public State(ScrollViewer sv) => _sv = sv;

        public void Attach()
        {
            _sv.ScrollChanged += OnScrollChanged;
            _sv.MouseMove += OnPointerActivity;
            _sv.Unloaded += OnUnloaded;
        }

        public void Detach()
        {
            _sv.ScrollChanged -= OnScrollChanged;
            _sv.MouseMove -= OnPointerActivity;
            _sv.Unloaded -= OnUnloaded;
            _timer?.Stop();
            // Without the behavior the bars must be usable: show them permanently (template rests at 0).
            SetBarsOpacity(1);
        }

        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // ScrollChanged bubbles from nested viewers (a list inside a page) — only react to our own offset moves.
            if (!ReferenceEquals(e.OriginalSource, _sv)) return;
            if (e.VerticalChange == 0 && e.HorizontalChange == 0) return;
            Show();
        }

        private void OnPointerActivity(object sender, System.Windows.Input.MouseEventArgs e) => Show();

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _timer?.Stop();
            _shown = false;
            SetBarsOpacity(0);
        }

        private void Show()
        {
            if (!AnyBarVisible()) return;
            if (!_shown)
            {
                _shown = true;
                foreach (var bar in Bars())
                    UiMotion.FadeTo(bar, 1, ms: UiMotion.FastMs);
            }
            RestartTimer();
        }

        private void RestartTimer()
        {
            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, _sv.Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(HoldMs)
                };
                _timer.Tick += OnTimerTick;
            }
            _timer.Stop();
            _timer.Start();
        }

        private void OnTimerTick(object? sender, EventArgs e)
        {
            // Keep the bar while it is hovered or its thumb is dragged (mouse captured inside the bar).
            foreach (var bar in Bars())
            {
                if (bar.IsMouseOver || bar.IsMouseCaptureWithin)
                    return; // timer keeps ticking; re-checked next interval
            }
            _timer?.Stop();
            _shown = false;
            foreach (var bar in Bars())
                UiMotion.FadeTo(bar, 0, ms: UiMotion.SlowMs);
        }

        private bool AnyBarVisible()
        {
            foreach (var bar in Bars())
                if (bar.Visibility == Visibility.Visible) return true;
            return false;
        }

        private void SetBarsOpacity(double value)
        {
            foreach (var bar in Bars())
            {
                bar.BeginAnimation(UIElement.OpacityProperty, null);
                bar.Opacity = value;
            }
        }

        private IEnumerable<WpfScrollBar> Bars()
        {
            WpfScrollBar? v = null, h = null;
            try
            {
                v = _sv.Template?.FindName("PART_VerticalScrollBar", _sv) as WpfScrollBar;
                h = _sv.Template?.FindName("PART_HorizontalScrollBar", _sv) as WpfScrollBar;
            }
            catch (InvalidOperationException)
            {
                // Template not applied to this viewer (yet) — nothing to drive.
            }
            if (v != null) yield return v;
            if (h != null) yield return h;
        }
    }
}
