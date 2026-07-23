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
        TxtArgs.Text = model.Arguments ?? "";
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
        string? args = string.IsNullOrWhiteSpace(TxtArgs.Text) ? null : TxtArgs.Text.Trim();
        string? task = string.IsNullOrWhiteSpace(TxtTask.Text) ? null : TxtTask.Text.Trim();

        // Allow pasting "exe + args" into the path box: split on first " .exe "
        if (!scheduled && TrySplitPathAndArgs(ref path, ref args))
        {
            TxtPath.Text = path;
            TxtArgs.Text = args ?? "";
        }

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

        if (!PathSecurity.IsSafeArguments(args))
        {
            ThemedDialog.Show(this, "Arguments contain unsafe characters (& | > < ^ ;).");
            return;
        }

        _model.Path = path;
        _model.Arguments = args;
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

    /// <summary>
    /// If path looks like: D:\app\foo.exe -launchapp id — move the tail into args.
    /// </summary>
    private static bool TrySplitPathAndArgs(ref string path, ref string? args)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (PathSecurity.IsPlausibleExecutablePath(path)) return false;

        int exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe < 0) return false;
        int after = exe + 4;
        if (after >= path.Length) return false;
        if (!char.IsWhiteSpace(path[after])) return false;

        string exePath = path[..after].Trim().Trim('"');
        string rest = path[after..].Trim();
        if (!PathSecurity.IsPlausibleExecutablePath(exePath)) return false;
        if (string.IsNullOrWhiteSpace(rest)) return false;

        path = exePath;
        if (string.IsNullOrWhiteSpace(args))
            args = rest;
        else
            args = rest + " " + args;
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
