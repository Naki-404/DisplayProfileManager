using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class PackGalleryWindow : Window
{
    /// <summary>Gallery row — wraps a bundled pack with UI-facing labels and enabled state.</summary>
    public sealed class PackItem : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string? Subtitle { get; set; }
        public string ApplyLabel { get; set; } = "Apply";
        public List<Models.QuickPreset> Presets { get; set; } = new();

        private bool _canApply;
        public bool CanApply
        {
            get => _canApply;
            set
            {
                if (_canApply == value) return;
                _canApply = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanApply)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<PackItem> _packs = new();
    private string? _targetGame;

    public PackGalleryWindow()
    {
        InitializeComponent();
        Opacity = 0;
        Loaded += (_, _) => UiMotion.PopIn(this);
        PacksList.ItemsSource = _packs;
        LoadPacks();
        ApplyLabels();
        AcceptTargetGame(null);
    }

    private void LoadPacks()
    {
        _packs.Clear();
        foreach (var pack in PresetPackService.LoadBundledPacks())
        {
            _packs.Add(new PackItem
            {
                Name = pack.Name,
                Subtitle = pack.Subtitle,
                ApplyLabel = Loc.T("btn.apply"),
                Presets = pack.Presets
            });
        }
    }

    /// <summary>Sets (or clears) the game these packs will be applied to; disables Apply when empty.</summary>
    public void AcceptTargetGame(string? name)
    {
        _targetGame = string.IsNullOrWhiteSpace(name) ? null : name;
        bool has = _targetGame != null;

        LblAppliesTo.Text = has
            ? Loc.Tf("pack.appliesTo", _targetGame!)
            : Loc.T("presets.pick.game");
        LblAppliesTo.Foreground = has
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("MutedBrush");

        foreach (var p in _packs)
            p.CanApply = has;
    }

    private void ApplyLabels()
    {
        Title = Loc.T("presets.gallery");
        TxtTitle.Text = Loc.T("presets.gallery");
        LblHint.Text = Loc.T("presets.gallery.hint");
        BtnClose.Content = Loc.T("btn.cancel");
        foreach (var p in _packs)
            p.ApplyLabel = Loc.T("btn.apply");
    }

    private void ApplyPack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: PackItem item }) return;
        Apply(item.Presets, item.Name);
    }

    private void Apply(System.Collections.Generic.List<Models.QuickPreset> presets, string name)
    {
        if (Owner is MainWindow mw)
        {
            mw.ApplyPackPresets(presets, name);
            DialogResult = true;
            Close();
            return;
        }
        ThemedDialog.Show(this, Loc.T("presets.pick.game"), Loc.T("presets.gallery"));
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
