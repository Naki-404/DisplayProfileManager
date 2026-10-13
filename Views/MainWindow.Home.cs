using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Visor.Models;
using Visor.Services;

namespace Visor;

/// <summary>Home page: dashboard cards, health, recommendations, quick games, activity.</summary>
public partial class MainWindow : Window
{
    /// <summary>Set when a refresh was skipped because the page was hidden; honored by RefreshStalePages.</summary>
    private bool _homeStale = true;

    private bool HomePageIsLive => IsVisible && ReferenceEquals(MainTabs.SelectedItem, TabHome);

    private void RefreshHomeDashboard()
    {
        if (TxtHomeSessionStatus == null) return;
        if (!HomePageIsLive)
        {
            _homeStale = true;
            return;
        }
        _homeStale = false;

        var monitor = App.Services.Monitor;
        var active = monitor.CurrentProfile;
        bool paused = monitor.IsPaused;

        if (paused)
        {
            TxtHomeSessionStatus.Text = Loc.T("home.session.paused");
            TxtHomeSessionDetail.Text = Loc.T("home.session.paused.detail");
        }
        else if (active != null)
        {
            TxtHomeSessionStatus.Text = Loc.Tf("home.session.active", active.Name);
            var bits = new List<string>();
            if (active.ApplyResolution && !string.IsNullOrWhiteSpace(active.Resolution))
                bits.Add(ColorUiHelper.FormatResolution(active.Resolution));
            if (monitor.HasActivePreset)
                bits.Add(Loc.T("home.session.presetOn"));
            TxtHomeSessionDetail.Text = bits.Count > 0
                ? string.Join(" · ", bits)
                : (active.ProcessName ?? "");
        }
        else
        {
            TxtHomeSessionStatus.Text = Loc.T("home.session.idle");
            TxtHomeSessionDetail.Text = Loc.T("home.session.idle.detail");
        }

        var mode = DisplayEngine.GetCurrentMode(App.Services.Config.Current.Ui?.PreferredDisplayDevice);
        TxtHomeDisplayMode.Text = mode.RefreshRate > 0
            ? Loc.Tf("home.display.mode", mode.Resolution, mode.RefreshRate)
            : mode.Resolution;
        TxtHomeGpu.Text = Loc.Tf("home.display.gpu", GpuVendorDetect.DriverLabel);

        int total = _profiles.Count;
        int enabled = _profiles.Count(p => p.Enabled);
        int presets = _profiles.Sum(p => p.Presets?.Count ?? 0);
        TxtHomeLibraryCounts.Text = Loc.Tf("home.library.counts",
            Loc.Plural("unit.games", total), enabled, Loc.Plural("unit.presets", presets));

        RefreshHomeHealth();
        RefreshHomeRecommendations();
        RefreshHomeQuickGames();
        RefreshHomeActivity();
    }

    private void RefreshHomeRecommendations()
    {
        if (HomeRecList == null) return;
        HomeRecList.Children.Clear();

        var store = UiStateStore.Current.DismissedRecommendations ?? new List<string>();
        var dismissed = new HashSet<string>(store, StringComparer.OrdinalIgnoreCase);
        // Conflicts / crash state are reported by the health block, not by rules — the context stays minimal.
        var net = NetworkInfoService.CapturePrimary();

        var ctx = new RecommendationContext
        {
            MonitoringPaused = App.Services.Monitor.IsPaused,
            ProfileCount = _profiles.Count,
            GameModeEnabled = GameModeSetting.Read(),
            HasUsableNetworkAdapter = net != null,
            HasUsableGateway = !string.IsNullOrEmpty(net?.Gateway),
            DismissedIds = dismissed
        };

        var recs = RecommendationEngine.Evaluate(ctx);
        TxtHomeRecEmpty.Visibility = recs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var rec in recs)
            HomeRecList.Children.Add(BuildRecommendationRow(rec));
        BtnHomeRecReset.Visibility = RecommendationDismissals.Parse(store, DateTime.UtcNow).Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Startup housekeeping: drop expired / legacy dismissal tokens (saved only when something changed).</summary>
    private static void PruneDismissedRecommendations()
    {
        try
        {
            var st = UiStateStore.Current;
            st.DismissedRecommendations ??= new List<string>();
            if (RecommendationDismissals.Prune(st.DismissedRecommendations, DateTime.UtcNow))
                UiStateStore.Save();
        }
        catch (Exception ex)
        {
            AppLog.Warn(ex, "Prune dismissed recommendations");
        }
    }

    private void RecReset_Click(object sender, RoutedEventArgs e)
    {
        var st = UiStateStore.Current;
        st.DismissedRecommendations ??= new List<string>();
        RecommendationEngine.ResetDismissed(st.DismissedRecommendations);
        UiStateStore.Save();
        RefreshHomeRecommendations();
    }

    private FrameworkElement BuildRecommendationRow(Recommendation rec)
    {
        var card = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            CornerRadius = new CornerRadius(6)
        };
        card.SetResourceReference(Border.BackgroundProperty, "FieldBrush");

        var root = new DockPanel();
        var actions = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Top
        };
        DockPanel.SetDock(actions, Dock.Right);

        var dismiss = new System.Windows.Controls.Button
        {
            Content = Loc.T("rec.dismiss"),
            Style = (Style)FindResource("CompactGhostButton"),
            Tag = rec,
            Margin = new Thickness(4, 0, 0, 0)
        };
        dismiss.Click += RecDismiss_Click;
        actions.Children.Add(dismiss);

        if (rec.Action != RecommendationAction.None && !string.IsNullOrEmpty(rec.ActionKey))
        {
            var apply = new System.Windows.Controls.Button
            {
                Content = Loc.T(rec.ActionKey),
                Style = (Style)FindResource("CompactAccentButton"),
                Tag = rec,
                Margin = new Thickness(0, 0, 0, 0)
            };
            apply.Click += RecApply_Click;
            actions.Children.Insert(0, apply);
        }

        root.Children.Add(actions);

        string body = FormatRecommendationBody(rec);
        var textCol = new StackPanel();
        var titleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        // Severity badge (Info / Tip / Attention) — colored by the same tokens as the health dots.
        var badge = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(1)
        };
        badge.SetResourceReference(Border.BorderBrushProperty,
            rec.Severity == RecommendationSeverity.Warning ? "WarningBrush" : "BorderBrush");
        badge.SetResourceReference(Border.BackgroundProperty, "PillBgBrush");
        var badgeText = new TextBlock { Text = Loc.T(rec.SeverityKey), FontSize = 10, FontWeight = FontWeights.SemiBold };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty,
            rec.Severity == RecommendationSeverity.Warning ? "WarningBrush" : "MutedBrush");
        badge.Child = badgeText;
        titleRow.Children.Add(badge);
        titleRow.Children.Add(new TextBlock
        {
            Text = Loc.T(rec.TitleKey),
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextBrush"),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        textCol.Children.Add(titleRow);
        textCol.Children.Add(new TextBlock
        {
            Text = body,
            Style = (Style)FindResource("Hint"),
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        root.Children.Add(textCol);
        card.Child = root;
        return card;
    }

    private static string FormatRecommendationBody(Recommendation rec)
    {
        if (rec.Id == RecommendationEngine.IdConflicts && rec.BodyArgs.Length >= 2)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(rec.BodyArgs[0]))
                parts.Add(Loc.Tf("rec.conflicts.apps", rec.BodyArgs[0]));
            if (rec.BodyArgs[1] == "1")
                parts.Add(Loc.T("rec.conflicts.nightLight"));
            return parts.Count > 0
                ? Loc.Tf("rec.conflicts.body.detail", string.Join(" · ", parts))
                : Loc.T("rec.conflicts.body");
        }
        return rec.BodyArgs.Length > 0
            ? Loc.Tf(rec.BodyKey, rec.BodyArgs.Cast<object>().ToArray())
            : Loc.T(rec.BodyKey);
    }

    private void RecDismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Recommendation rec }) return;
        var st = UiStateStore.Current;
        st.DismissedRecommendations ??= new List<string>();
        // Dismissal is per occurrence (id + context hash) and expires — see RecommendationDismissals.
        RecommendationDismissals.Add(st.DismissedRecommendations, rec, DateTime.UtcNow);
        UiStateStore.Save();
        RefreshHomeRecommendations();
    }

    private void RecApply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: Recommendation rec }) return;
        switch (rec.Action)
        {
            case RecommendationAction.ResumeMonitoring:
                if (App.Services.Monitor.IsPaused)
                    PauseMonitoring_Click(sender, e);
                RefreshHomeDashboard();
                break;
            case RecommendationAction.SafeActionEnableGameMode:
                MainTabs.SelectedItem = TabSystem;
                if (GameModeSetting.Read() == false)
                    GameModeToggle_Click(BtnGameModeToggle, e);
                break;
            case RecommendationAction.OpenSystem:
                MainTabs.SelectedItem = TabSystem;
                break;
            case RecommendationAction.OpenNetwork:
                MainTabs.SelectedItem = TabNetwork;
                break;
            case RecommendationAction.OpenGames:
                MainTabs.SelectedItem = TabGames;
                break;
        }
        RefreshHomeRecommendations();
    }

    private ConflictDetector.Result? _homeConflicts;
    private DateTime _homeConflictsUtc = DateTime.MinValue;

    private void RefreshHomeHealth()
    {
        if (HomeHealthDots == null) return;
        HomeHealthDots.Children.Clear();

        bool paused = App.Services.Monitor.IsPaused;
        AddHomeHealthRow(
            paused ? "WarningBrush" : "SuccessBrush",
            Loc.T(paused ? "home.health.paused" : "home.health.monitoring"));

        AddHomeHealthRow(
            GpuVendorDetect.IsDetected ? "SuccessBrush" : "MutedBrush",
            Loc.Tf("home.health.gpu", GpuVendorDetect.DriverLabel));

        if (_homeConflicts == null || (DateTime.UtcNow - _homeConflictsUtc).TotalSeconds > 5)
        {
            _homeConflicts = ConflictDetector.Scan();
            _homeConflictsUtc = DateTime.UtcNow;
        }
        var conflicts = _homeConflicts;
        if (conflicts.HasAny)
        {
            var parts = new List<string>();
            if (conflicts.ConflictingApps.Count > 0)
                parts.Add(Loc.Tf("home.health.conflict.apps", string.Join(", ", conflicts.ConflictingApps)));
            if (conflicts.NightLightOn)
                parts.Add(Loc.T("home.health.conflict.nightLight"));
            AddHomeHealthRow("WarningBrush", parts.Count > 0
                ? string.Join(" · ", parts)
                : Loc.T("home.health.conflict"));
        }
        else
        {
            AddHomeHealthRow("SuccessBrush", Loc.T("home.health.clear"));
        }

        if (App.Services.CrashRestored)
            AddHomeHealthRow("AccentBrush", Loc.T("home.health.crashRestored"));
    }

    private void AddHomeHealthRow(string brushKey, string text)
    {
        var row = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        row.Children.Add(dot);
        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        row.Children.Add(label);
        HomeHealthDots.Children.Add(row);
    }

    private void RefreshHomeQuickGames()
    {
        if (HomeQuickList == null) return;
        HomeQuickList.Children.Clear();

        // Real recent activity only — never pad with arbitrary library entries.
        var recentIds = App.Services.History.Recent(40)
            .Where(e => !string.IsNullOrEmpty(e.ProfileId)
                        && (e.Kind is "profileApplied" or "presetApplied" or "sessionRestored"))
            .Select(e => e.ProfileId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        var games = new List<GameProfile>();
        foreach (var id in recentIds)
        {
            var match = _profiles.FirstOrDefault(p => p.Id == id);
            if (match != null) games.Add(match);
        }

        TxtHomeQuickEmpty.Visibility = games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var g in games)
        {
            var profile = g;
            var btn = new System.Windows.Controls.Button
            {
                Content = profile.Name,
                Style = (Style)FindResource("CompactGhostButton"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 4),
                ToolTip = profile.ProcessName
            };
            btn.Click += (_, _) =>
            {
                MainTabs.SelectedItem = TabGames;
                SelectProfile(profile);
            };
            HomeQuickList.Children.Add(btn);
        }
    }

    private void RefreshHomeActivity()
    {
        if (HomeActivityList == null) return;
        HomeActivityList.Children.Clear();
        var recent = App.Services.History.Recent(8);
        TxtHomeActivityEmpty.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in recent)
        {
            HomeActivityList.Children.Add(new TextBlock
            {
                Text = FormatHistoryEntry(entry),
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            });
        }
    }

    private void HomeAddGame_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabGames;
        AddProfile_Click(sender, e);
    }

    private void HomeOpenGames_Click(object sender, RoutedEventArgs e) =>
        MainTabs.SelectedItem = TabGames;

    /// <summary>Home sits next to Pause — a misclick must not reset the display without asking.</summary>
    private void HomeEmergencyRestore_Click(object sender, RoutedEventArgs e)
    {
        if (!ThemedDialog.Confirm(this, Loc.T("confirm.emergency"), Loc.T("btn.emergency"), Loc.T("btn.emergency"), danger: true))
            return;
        EmergencyRestore_Click(sender, e);
    }

    private void HomeOpenHistory_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = TabMore;
        RbMoreHistory.IsChecked = true;
        RefreshHistoryPanel();
    }
}
