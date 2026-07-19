using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DisplayProfileManager;

public partial class ProcessPickerWindow : Window
{
    private readonly List<string> _all;

    public string? SelectedProcess { get; private set; }

    public ProcessPickerWindow(IEnumerable<string> processes)
    {
        InitializeComponent();
        Opacity = 0;
        Loaded += (_, _) => UiMotion.PopIn(this);
        _all = processes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        ProcList.ItemsSource = _all;
    }

    private void TxtFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        var q = TxtFilter.Text.Trim();
        ProcList.ItemsSource = string.IsNullOrEmpty(q)
            ? _all
            : _all.Where(p => p.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        SelectedProcess = ProcList.SelectedItem as string;
        DialogResult = SelectedProcess != null;
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
