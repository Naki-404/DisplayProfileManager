using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Visor.Services;

namespace Visor;

public partial class ProcessPickerWindow : Window
{
    private readonly List<string> _all;

    public string? SelectedProcess { get; private set; }

    public ProcessPickerWindow(IEnumerable<string> processes)
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            OverlayWin32.EnsureInsideWorkArea(this);
            UiMotion.PopIn(ChromeRoot);
            TxtFilter.Focus();
        };
        _all = processes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        ProcList.ItemsSource = _all;

        Title = Loc.T("picker.title");
        TxtTitle.Text = Loc.T("picker.title");
        TxtFilter.Tag = Loc.T("picker.filter");
        BtnCancel.Content = Loc.T("btn.cancel");
        BtnSelect.Content = Loc.T("picker.select");
        BtnClose.ToolTip = Loc.T("btn.close");
        System.Windows.Automation.AutomationProperties.SetName(BtnClose, Loc.T("btn.close"));
        UpdateEmpty();
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = TxtFilter.Text.Trim();
        ProcList.ItemsSource = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ProcList.Items.Count == 1)
            ProcList.SelectedIndex = 0;
        UpdateEmpty();
    }

    /// <summary>Arrow keys in the filter move the list selection — no mouse needed.</summary>
    private void TxtFilter_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (ProcList.Items.Count == 0) return;
        if (e.Key == Key.Down)
        {
            ProcList.SelectedIndex = Math.Min(ProcList.Items.Count - 1, ProcList.SelectedIndex + 1);
            ProcList.ScrollIntoView(ProcList.SelectedItem);
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            ProcList.SelectedIndex = Math.Max(0, ProcList.SelectedIndex - 1);
            ProcList.ScrollIntoView(ProcList.SelectedItem);
            e.Handled = true;
        }
    }

    private void UpdateEmpty()
    {
        bool empty = ProcList.Items.Count == 0;
        TxtEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TxtEmpty.Text = Loc.T("picker.empty");
        BtnSelect.IsEnabled = ProcList.SelectedItem != null;
    }

    private void ProcList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => BtnSelect.IsEnabled = ProcList.SelectedItem != null;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedProcess = ProcList.SelectedItem as string;
        if (SelectedProcess == null) return;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ProcList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProcList.SelectedItem != null)
            Ok_Click(sender, e);
    }
}
