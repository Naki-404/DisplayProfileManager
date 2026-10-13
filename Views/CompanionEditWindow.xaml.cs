using System.Windows;
using System.Windows.Controls;
using Visor.Models;
using Visor.Services;

namespace Visor;

public partial class CompanionEditWindow : Window
{
    private readonly CompanionApp _model;

    public CompanionEditWindow(CompanionApp model)
    {
        InitializeComponent();
        Opacity = 0;
        Loaded += (_, _) => UiMotion.PopIn(this);
        _model = model;
        ApplyLabels();

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
        UpdateModePanels();
    }

    private void ApplyLabels()
    {
        Title = Loc.T("companion.title");
        TxtTitle.Text = Loc.T("companion.title");
        BtnClose.ToolTip = Loc.T("btn.close");
        LblLaunch.Text = Loc.T("companion.launch");
        ItmDirect.Content = Loc.T("companion.launch.direct");
        ItmTask.Content = Loc.T("companion.launch.task");
        LblPath.Text = Loc.T("companion.path");
        BtnBrowse.Content = Loc.T("btn.browse");
        LblArgs.Text = Loc.T("companion.args");
        LblArgsHint.Text = Loc.T("companion.args.hint");
        TxtArgs.Tag = "-launchapp …";
        LblTask.Text = Loc.T("companion.task");
        LblTaskHint.Text = Loc.T("companion.task.hint");
        LblOnStop.Text = Loc.T("companion.onStop");
        ItmCloseKill.Content = Loc.T("companion.stop.closeThenKill");
        ItmClose.Content = Loc.T("companion.stop.close");
        ItmKill.Content = Loc.T("companion.stop.kill");
        ItmNone.Content = Loc.T("companion.stop.none");
        ChkDismiss.Content = Loc.T("companion.dismiss");
        ChkTray.Content = Loc.T("companion.tray");
        LblSafety.Text = Loc.T("companion.safety");
        BtnCancel.Content = Loc.T("btn.cancel");
        BtnOk.Content = Loc.T("btn.save");
    }

    private void CmbLaunch_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        UpdateModePanels();
    }

    private void UpdateModePanels()
    {
        bool scheduled = CmbLaunch.SelectedIndex == 1;
        PanelDirect.Visibility = scheduled ? Visibility.Collapsed : Visibility.Visible;
        PanelTask.Visibility = scheduled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.T("filter.exe"),
            Title = Loc.T("companion.path")
        };
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
                ThemedDialog.Show(this, Loc.T("companion.err.task"), Loc.T("companion.title"));
                return;
            }
        }
        else
        {
            if (!PathSecurity.IsPlausibleExecutablePath(path))
            {
                ThemedDialog.Show(this, Loc.T("companion.err.path"), Loc.T("companion.title"));
                return;
            }
        }

        if (!PathSecurity.IsSafeArguments(args))
        {
            ThemedDialog.Show(this, Loc.T("companion.err.args"), Loc.T("companion.title"));
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

    /// <summary>Friendly one-line label for the companion list (instead of raw enum values).</summary>
    public static string Describe(CompanionApp c)
    {
        string name = string.Equals(c.LaunchMode, "scheduledTask", StringComparison.OrdinalIgnoreCase)
            ? Loc.Tf("companion.list.task", c.TaskName ?? "?")
            : System.IO.Path.GetFileName(c.Path ?? "");
        if (string.IsNullOrWhiteSpace(name)) name = Loc.T("companion.list.unnamed");
        string stop = (c.OnStop ?? "").ToLowerInvariant() switch
        {
            "close" => Loc.T("companion.stop.close.short"),
            "kill" => Loc.T("companion.stop.kill.short"),
            "none" => Loc.T("companion.stop.none.short"),
            _ => Loc.T("companion.stop.closeThenKill.short")
        };
        return $"{name}  ·  {stop}";
    }
}
