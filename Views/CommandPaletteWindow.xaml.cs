using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Visor.Services;

namespace Visor;

public partial class CommandPaletteWindow : Window
{
    private const int FallbackPageSize = 8;

    private readonly List<CommandPaletteItem> _all;
    private List<CommandPaletteItem> _filtered = new();
    private bool _closing;

    public CommandPaletteItem? Selected { get; private set; }

    /// <param name="isEnabled">
    /// UI enablement seam (id → enabled). Disabled commands stay visible but greyed and are never
    /// returned as <see cref="Selected"/> (e.g. "cmd.scan" while a scan is running).
    /// </param>
    public CommandPaletteWindow(IEnumerable<CommandPaletteItem> items, Func<string, bool>? isEnabled = null)
    {
        InitializeComponent();
        _all = CommandPaletteCatalog.ApplyEnabled(items, isEnabled).ToList();
        ApplyLocalization();
        RefreshList();
        Loaded += (_, _) =>
        {
            OverlayWin32.EnsureInsideWorkArea(this);
            TxtFilter.Focus();
            UiMotion.PopIn(ChromeRoot);
        };
        // Clicking the owner / another app: the palette is transient — dismiss instead of lingering.
        Deactivated += (_, _) => Dismiss();
    }

    private void ApplyLocalization()
    {
        Title = Loc.T("command.palette.title");
        TxtFilter.Tag = Loc.T("command.palette.filter");
        TxtHint.Text = Loc.T("command.palette.hint");
        TxtEmpty.Text = Loc.T("command.palette.empty");
        System.Windows.Automation.AutomationProperties.SetName(TxtFilter, Loc.T("command.palette.filter"));
    }

    private void RefreshList()
    {
        _filtered = CommandPaletteCatalog.Filter(TxtFilter.Text, _all).ToList();
        CmdList.ItemsSource = _filtered;
        TxtEmpty.Visibility = _filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_filtered.Count > 0)
            CmdList.SelectedIndex = Math.Max(0, _filtered.FindIndex(i => i.IsEnabled));
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e) => RefreshList();

    private void TxtFilter_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (HandleNavigationKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Dismiss();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            Accept();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)
        {
            if (!ReferenceEquals(Keyboard.FocusedElement, TxtFilter)
                && !ReferenceEquals(Keyboard.FocusedElement, CmdList))
            {
                HandleNavigationKey(e.Key);
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Back && !ReferenceEquals(Keyboard.FocusedElement, TxtFilter))
        {
            FocusFilter();
            if (TxtFilter.Text.Length > 0)
            {
                TxtFilter.Text = TxtFilter.Text[..^1];
                TxtFilter.CaretIndex = TxtFilter.Text.Length;
            }
            e.Handled = true;
        }
    }

    /// <summary>Up/Down move by one, PageUp/PageDown by a visible page, Home/End to the ends.</summary>
    private bool HandleNavigationKey(Key key)
    {
        switch (key)
        {
            case Key.Down: MoveSelection(1); return true;
            case Key.Up: MoveSelection(-1); return true;
            case Key.PageDown: MoveSelection(PageSize()); return true;
            case Key.PageUp: MoveSelection(-PageSize()); return true;
            case Key.Home: MoveSelection(-_filtered.Count); return true;
            case Key.End: MoveSelection(_filtered.Count); return true;
            default: return false;
        }
    }

    private int PageSize()
    {
        try
        {
            double listHeight = CmdList.ActualHeight;
            if (listHeight <= 0) return FallbackPageSize;
            double rowHeight = 0;
            if (CmdList.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, CmdList.SelectedIndex)) is ListBoxItem row
                && row.ActualHeight > 0)
                rowHeight = row.ActualHeight + row.Margin.Top + row.Margin.Bottom;
            if (rowHeight <= 0) return FallbackPageSize;
            return Math.Max(1, (int)Math.Floor(listHeight / rowHeight));
        }
        catch
        {
            return FallbackPageSize;
        }
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (!ReferenceEquals(Keyboard.FocusedElement, TxtFilter) && !string.IsNullOrEmpty(e.Text))
        {
            FocusFilter();
            int caret = TxtFilter.SelectionStart;
            string before = TxtFilter.Text ?? "";
            int selLen = TxtFilter.SelectionLength;
            TxtFilter.Text = before[..caret] + e.Text + before[(caret + selLen)..];
            TxtFilter.CaretIndex = caret + e.Text.Length;
            e.Handled = true;
            return;
        }
        base.OnPreviewTextInput(e);
    }

    private void FocusFilter()
    {
        TxtFilter.Focus();
        TxtFilter.CaretIndex = TxtFilter.Text?.Length ?? 0;
    }

    private void MoveSelection(int delta)
    {
        if (_filtered.Count == 0) return;
        int next = Math.Clamp(CmdList.SelectedIndex + delta, 0, _filtered.Count - 1);
        CmdList.SelectedIndex = next;
        if (CmdList.SelectedItem != null)
            CmdList.ScrollIntoView(CmdList.SelectedItem);
    }

    private void CmdList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (CmdList.SelectedItem is not CommandPaletteItem item) return;
        if (!item.IsEnabled) return; // greyed: never executed
        Selected = item;
        Close(true);
    }

    private void Dismiss() => Close(false);

    private void Close(bool result)
    {
        if (_closing) return;
        _closing = true;
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // Not shown via ShowDialog (or already closing) — plain close.
            try { Close(); } catch { /* ignore */ }
        }
    }
}
