namespace DisplayProfileManager.Setup;

internal sealed class SetupForm : Form
{
    private readonly TextBox _path;
    private readonly CheckBox _startMenu;
    private readonly CheckBox _desktop;
    private readonly CheckBox _launch;
    private readonly Label _status;
    private readonly Button _install;
    private readonly Label _runtimeWarn;

    public SetupForm()
    {
        Text = "Display Profile Manager Setup";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 300);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(0xF7, 0xF4, 0xF6);
        ForeColor = Color.FromArgb(0x2A, 0x1C, 0x24);

        var title = new Label
        {
            Text = "Display Profile Manager",
            Font = new Font("Segoe UI Semibold", 16f),
            AutoSize = true,
            Location = new Point(20, 16)
        };
        var sub = new Label
        {
            Text = "Version 1.4.0 — compact single-file install",
            ForeColor = Color.FromArgb(0x80, 0x60, 0x70),
            AutoSize = true,
            Location = new Point(22, 48)
        };

        var pathLbl = new Label { Text = "Install folder", AutoSize = true, Location = new Point(22, 84) };
        _path = new TextBox
        {
            Text = Program.DefaultInstallDir,
            Location = new Point(22, 104),
            Width = 340
        };
        var browse = new Button
        {
            Text = "…",
            Location = new Point(370, 102),
            Width = 60,
            Height = 28
        };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = _path.Text };
            if (d.ShowDialog(this) == DialogResult.OK)
                _path.Text = d.SelectedPath;
        };

        _startMenu = new CheckBox { Text = "Start Menu shortcut", Checked = true, AutoSize = true, Location = new Point(22, 146) };
        _desktop = new CheckBox { Text = "Desktop shortcut", Checked = false, AutoSize = true, Location = new Point(22, 170) };
        _launch = new CheckBox { Text = "Launch after install", Checked = true, AutoSize = true, Location = new Point(22, 194) };

        _runtimeWarn = new Label
        {
            AutoSize = false,
            Width = 410,
            Height = 36,
            Location = new Point(22, 220),
            ForeColor = Color.FromArgb(0xA0, 0x40, 0x50),
            Text = ""
        };

        _status = new Label { Text = "", AutoSize = true, Location = new Point(22, 258), ForeColor = Color.FromArgb(0x60, 0x50, 0x58) };

        _install = new Button
        {
            Text = "Install",
            Width = 110,
            Height = 32,
            Location = new Point(320, 250),
            BackColor = Color.FromArgb(0xC4, 0x5C, 0x84),
            ForeColor = Color.White,
            FlatStyle = FlatStyle.Flat
        };
        _install.FlatAppearance.BorderSize = 0;
        _install.Click += Install_Click;

        var cancel = new Button
        {
            Text = "Cancel",
            Width = 90,
            Height = 32,
            Location = new Point(220, 250),
            DialogResult = DialogResult.Cancel
        };
        cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            title, sub, pathLbl, _path, browse, _startMenu, _desktop, _launch,
            _runtimeWarn, _status, cancel, _install
        });

        if (!Program.HasDesktopRuntime())
        {
            _runtimeWarn.Text = ".NET Desktop Runtime not found. Install will open the download page first.";
            var link = new LinkLabel
            {
                Text = "Download runtime",
                AutoSize = true,
                Location = new Point(22, 238)
            };
            link.Click += (_, _) => Program.OpenRuntimeDownload();
            Controls.Add(link);
        }
    }

    private async void Install_Click(object? sender, EventArgs e)
    {
        _install.Enabled = false;
        try
        {
            if (!Program.HasDesktopRuntime())
            {
                Program.OpenRuntimeDownload();
                MessageBox.Show(this,
                    "Install the .NET Desktop Runtime, then click Install again.",
                    Text);
                return;
            }

            var dir = _path.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir))
            {
                MessageBox.Show(this, "Choose an install folder.", Text);
                return;
            }

            await Task.Run(() => Program.Install(dir, _startMenu.Checked, _desktop.Checked, false, s =>
            {
                BeginInvoke(() => _status.Text = s);
            }));

            if (_launch.Checked)
            {
                var exe = Path.Combine(dir, "DisplayProfileManager.exe");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true
                });
            }

            MessageBox.Show(this, "Installation complete.", Text);
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Install failed:\n" + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _install.Enabled = true;
        }
    }
}
