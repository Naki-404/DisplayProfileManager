using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Visor.Controls;
using Visor.Services;
using WpfButton = System.Windows.Controls.Button;
using WpfRadioButton = System.Windows.Controls.RadioButton;

namespace Visor;

/// <summary>
/// Stability events card: the crash-related System-log records Visor already counts, listed in-app (newest first,
/// filter by kind, expand for the full rendered text, copy as plain text for support). Read-only; the read
/// (<see cref="CrashEventReader.ReadRecent"/>, incl. <c>FormatDescription</c>) runs off the UI thread. Replaces the
/// former "Open Event Viewer" button.
/// </summary>
public partial class MyPcPage
{
    private CrashEventList? _events;
    private CrashEventKind? _eventFilter;
    private bool _eventsLoading;
    private int _eventsGen;

    private bool EventsOpen => CardEvents.Visibility == Visibility.Visible;

    private void LocalizeEvents()
    {
        BtnShowEvents.Content = Loc.T(EventsOpen ? "hw.events.hide" : "hw.events.show");
        LblEventsTitle.Text = Loc.T("hw.events.title");
        TxtEventsHint.Text = Loc.Tf("hw.events.hint", (int)Math.Round(CrashEventReader.DefaultWindow.TotalDays));
        BtnEventsRefresh.Content = Loc.T("system.refresh");
        BtnEventsCopy.Content = Loc.T("hw.events.copy");
        BtnEventsCopy.ToolTip = Loc.T("hw.events.copy.tip");
        if (EventsOpen) RenderEvents();
    }

    private void ShowEvents_Click(object sender, RoutedEventArgs e)
    {
        bool open = !EventsOpen;
        CardEvents.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        BtnShowEvents.Content = Loc.T(open ? "hw.events.hide" : "hw.events.show");
        if (!open) return;
        RenderEvents();
        if (_events == null) _ = LoadEventsAsync();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => CardEvents.BringIntoView());
    }

    private void EventsRefresh_Click(object sender, RoutedEventArgs e) => _ = LoadEventsAsync();

    /// <summary>Reads the newest records off the UI thread (newest call wins).</summary>
    private async Task LoadEventsAsync()
    {
        int gen = ++_eventsGen;
        _eventsLoading = true;
        BtnEventsRefresh.IsEnabled = false;
        RenderEvents();
        CrashEventList list;
        try
        {
            list = await Task.Run(() => CrashEventReader.ReadRecent(CrashEventReader.DefaultRecentCount));
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Stability events");
            list = new CrashEventList { Reason = UnavailableReason.QueryFailed };
        }
        if (gen != _eventsGen) return;
        _eventsLoading = false;
        BtnEventsRefresh.IsEnabled = true;
        _events = list;
        if (_eventFilter is { } k && list.Events.All(x => x.Kind != k)) _eventFilter = null;
        RenderEvents();
    }

    private void RenderEvents()
    {
        EventsList.Children.Clear();
        EventsFilter.Children.Clear();
        var list = _events;
        BtnEventsCopy.IsEnabled = list is { Reason: UnavailableReason.None, Events.Count: > 0 } && !_eventsLoading;
        if (list == null || _eventsLoading)
        {
            TxtEventsStatus.Text = Loc.T("hw.events.loading");
            TxtEventsStatus.ToolTip = null;
            if (list == null) return;
        }
        if (list.Reason != UnavailableReason.None)
        {
            TxtEventsStatus.Text = Loc.T("hw.stability.unavailable");
            TxtEventsStatus.ToolTip = HardwareText.ReasonText(list.Reason);
            return;
        }
        TxtEventsStatus.ToolTip = null;
        if (list.Events.Count == 0)
        {
            if (!_eventsLoading) TxtEventsStatus.Text = Loc.T("hw.events.empty");
            return;
        }

        // Filter strip: All + only the kinds that occur.
        EventsFilter.Children.Add(FilterRadio(Loc.T("hw.events.filter.all"), null));
        foreach (var kind in CrashEventReader.KindsPresent(list.Events))
            EventsFilter.Children.Add(FilterRadio(Loc.T(CrashEventReader.KindLocKey(kind)), kind));
        RenderEventList();
    }

    /// <summary>Rows + count line for the current filter (the strip stays, so arrow-key navigation keeps focus).</summary>
    private void RenderEventList()
    {
        EventsList.Children.Clear();
        var list = _events;
        if (list == null || list.Reason != UnavailableReason.None) return;
        var shown = CrashEventReader.Filter(list.Events, _eventFilter);
        if (!_eventsLoading)
        {
            string status = Loc.Tf("hw.events.shown", shown.Count);
            if (list.Truncated) status += " · " + Loc.Tf("hw.events.truncated", CrashEventReader.DefaultRecentCount);
            TxtEventsStatus.Text = shown.Count == 0 ? Loc.T("hw.events.emptyFilter") : status;
        }
        foreach (var ev in shown)
            EventsList.Children.Add(BuildEventRow(ev));
    }

    private WpfRadioButton FilterRadio(string text, CrashEventKind? kind)
    {
        var rb = new WpfRadioButton
        {
            Content = text,
            GroupName = "EventsFilter",
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, 0, 4),
            IsChecked = _eventFilter == kind
        };
        rb.SetResourceReference(StyleProperty, "SegmentRadio");
        rb.Checked += (_, _) =>
        {
            if (_eventFilter == kind) return;
            _eventFilter = kind;
            RenderEventList();
        };
        return rb;
    }

    private static bool IsSerious(CrashEventKind k) =>
        k is CrashEventKind.UnexpectedShutdown or CrashEventKind.WheaFatal or CrashEventKind.DisplayTdr;

    private FrameworkElement BuildEventRow(CrashEventDetail ev)
    {
        var card = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(8)
        };
        card.SetResourceReference(Border.BackgroundProperty, "FieldBrush");
        var root = new DockPanel();

        string full = ev.Message.Length > 0 ? ev.Message : Loc.T("hw.events.noText");
        string first = ev.Message.Length > 0 ? ev.FirstLine : full;
        bool expandable = ev.Message.Length > 0 && !string.Equals(first, ev.Message.Trim(), StringComparison.Ordinal);

        var body = new StackPanel();
        var oneLine = new TextBlock
        {
            Text = first,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0),
            ToolTip = first
        };
        oneLine.SetResourceReference(TextBlock.ForegroundProperty, ev.Message.Length > 0 ? "TextBrush" : "MutedBrush");
        var fullText = new TextBlock
        {
            Text = full,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
            Visibility = Visibility.Collapsed
        };
        fullText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");

        if (expandable)
        {
            var toggle = new WpfButton { Content = Loc.T("hw.events.more"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 0, 0, 0) };
            toggle.SetResourceReference(StyleProperty, "LinkButton");
            AutomationProperties.SetName(toggle, Loc.T("hw.events.more") + " · " + Loc.T(CrashEventReader.KindLocKey(ev.Kind)));
            toggle.Click += (_, _) =>
            {
                bool open = fullText.Visibility != Visibility.Visible;
                fullText.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                oneLine.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
                toggle.Content = Loc.T(open ? "hw.events.less" : "hw.events.more");
            };
            DockPanel.SetDock(toggle, Dock.Right);
            root.Children.Add(toggle);
        }

        var head = new DockPanel();
        bool serious = IsSerious(ev.Kind);
        var badge = TweakRow.Badge(Loc.T(CrashEventReader.KindLocKey(ev.Kind)),
            serious ? "WarningBrush" : "BorderBrush", serious ? "WarningBrush" : "MutedBrush");
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        string when = ev.TimeUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var meta = TweakRow.Hint(Loc.Tf("hw.events.meta", when, ev.Provider, ev.EventId), new Thickness(0));
        meta.VerticalAlignment = VerticalAlignment.Center;
        meta.TextWrapping = TextWrapping.Wrap;
        head.Children.Add(meta);
        body.Children.Add(head);
        body.Children.Add(oneLine);
        body.Children.Add(fullText);
        root.Children.Add(body);
        card.Child = root;
        return card;
    }

    /// <summary>Copies the events currently shown (filter applied) as invariant plain text.</summary>
    private void EventsCopy_Click(object sender, RoutedEventArgs e)
    {
        var list = _events;
        if (list == null || list.Reason != UnavailableReason.None) return;
        var shown = CrashEventReader.Filter(list.Events, _eventFilter);
        if (shown.Count == 0) return;
        string text = CrashEventReader.BuildSupportReport(shown, DateTime.UtcNow, list.ToUtc - list.FromUtc);
        try
        {
            System.Windows.Clipboard.SetText(text);
            Host.ShowToast(Loc.T("hw.events.copied"));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or InvalidOperationException)
        {
            AppLog.Warn(ex, "Copy stability events");
            Host.ShowToast(Loc.T("hw.events.copyFail"));
        }
    }
}
