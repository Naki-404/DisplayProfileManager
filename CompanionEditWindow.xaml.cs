using System.Windows;
using DisplayProfileManager.Models;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class CompanionEditWindow : Window
{
    private readonly CompanionApp _model;

    public CompanionEditWindow(CompanionApp model)
    {
        InitializeComponent();
        Opacity = 0;
        Loaded += (_, _) => UiMotion.PopIn(this);
        _model = model;
        TxtPath.Text = model.Path;
        CmbLaunch.SelectedIndex = string.Equals(model.LaunchMode, "scheduledTask", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        TxtTask.Text = model.TaskName ?? "";
        CmbOnStop.SelectedIndex = model.OnStop?.ToLowerInvariant() switch
        {
            "close" => 1,
            "kill" => 2,
            "none" => 3,
            _ => 0 // closeThenKill / legacy hotkeyThenKill
        };
        ChkDismiss.IsChecked = model.DismissDialogs;
        ChkTray.IsChecked = model.MinimizeToTray;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Executable (*.exe)|*.exe" };
        if (dlg.ShowDialog() == true)
            TxtPath.Text = dlg.FileName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        bool scheduled = CmbLaunch.SelectedIndex == 1;
        string path = TxtPath.Text.Trim();
        string? task = string.IsNullOrWhiteSpace(TxtTask.Text) ? null : TxtTask.Text.Trim();

        if (scheduled)
        {
            if (!PathSecurity.IsSafeScheduledTaskName(task))
            {
                ThemedDialog.Show(this, "Scheduled task name is missing or unsafe.");
                return;
            }
        }
        else
        {
            if (!PathSecurity.IsPlausibleExecutablePath(path))
            {
                ThemedDialog.Show(this, "Companion must be a plain .exe path (no scripts or shell characters).");
                return;
            }
        }

        _model.Path = path;
        _model.LaunchMode = scheduled ? "scheduledTask" : "direct";
        _model.TaskName = task;
        _model.OnStop = CmbOnStop.SelectedIndex switch
        {
            1 => "close",
            2 => "kill",
            3 => "none",
            _ => "closeThenKill"
        };
        _model.StopHotkey = null;
        _model.DismissDialogs = ChkDismiss.IsChecked == true;
        _model.MinimizeToTray = ChkTray.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
