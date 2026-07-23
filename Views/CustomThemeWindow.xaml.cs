using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DisplayProfileManager.Models;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class CustomThemeWindow : Window
{
    private readonly Dictionary<string, System.Windows.Controls.TextBox> _boxes = new(StringComparer.OrdinalIgnoreCase);
    private ThemePalette _working;
    private readonly ThemePalette _originalSnapshot;
    public ThemePalette? ResultPalette { get; private set; }

    private static readonly (string Key, string En, string Ru, Func<ThemePalette, string> Get, Action<ThemePalette, string> Set)[] Fields =
    {
        ("Bg", "Background", "Фон", p => p.Bg, (p, v) => p.Bg = v),
        ("Panel", "Panels", "Панели", p => p.Panel, (p, v) => p.Panel = v),
        ("TitleBar", "Title bar", "Шапка окна", p => p.TitleBar, (p, v) => p.TitleBar = v),
        ("Border", "Borders", "Рамки", p => p.Border, (p, v) => p.Border = v),
        ("Text", "Text", "Текст", p => p.Text, (p, v) => p.Text = v),
        ("Muted", "Muted text", "Вторичный текст", p => p.Muted, (p, v) => p.Muted = v),
        ("Accent", "Accent / primary button", "Акцент / главная кнопка", p => p.Accent, (p, v) => p.Accent = v),
        ("AccentHover", "Accent hover", "Акцент (hover)", p => p.AccentHover, (p, v) => p.AccentHover = v),
        ("AccentButtonText", "Accent button text", "Текст на акценте", p => p.AccentButtonText, (p, v) => p.AccentButtonText = v),
        ("GhostBg", "Ghost button fill", "Фон ghost-кнопки", p => p.GhostBg, (p, v) => p.GhostBg = v),
        ("GhostBorder", "Ghost button border", "Рамка ghost-кнопки", p => p.GhostBorder, (p, v) => p.GhostBorder = v),
        ("GhostHover", "Ghost button hover", "Hover ghost-кнопки", p => p.GhostHover, (p, v) => p.GhostHover = v),
        ("Field", "Input fields", "Поля ввода", p => p.Field, (p, v) => p.Field = v),
        ("Track", "Slider track", "Трек слайдера", p => p.Track, (p, v) => p.Track = v),
        ("CheckCheckedBg", "Checkbox checked fill", "Чекбокс (вкл)", p => p.CheckCheckedBg, (p, v) => p.CheckCheckedBg = v),
        ("ComboHighlight", "Dropdown hover", "Выпадающий список hover", p => p.ComboHighlight, (p, v) => p.ComboHighlight = v),
        ("ComboSelected", "Dropdown selected", "Выпадающий список выбран", p => p.ComboSelected, (p, v) => p.ComboSelected = v),
        ("TabSelected", "Tab selected", "Вкладка активная", p => p.TabSelected, (p, v) => p.TabSelected = v),
        ("TabHover", "Tab hover", "Вкладка hover", p => p.TabHover, (p, v) => p.TabHover = v),
        ("CaptionHover", "Window buttons hover", "Кнопки окна hover", p => p.CaptionHover, (p, v) => p.CaptionHover = v),
        ("PillBg", "Active pill", "Плашка «активно»", p => p.PillBg, (p, v) => p.PillBg = v),
        ("ToastBg", "Toast background", "Фон уведомления", p => p.ToastBg, (p, v) => p.ToastBg = v),
        ("ToastBorder", "Toast border", "Рамка уведомления", p => p.ToastBorder, (p, v) => p.ToastBorder = v),
        ("Danger", "Danger / close hover", "Опасный / закрытие", p => p.Danger, (p, v) => p.Danger = v),
    };

    public CustomThemeWindow(ThemePalette initial)
    {
        InitializeComponent();
        Opacity = 0;
        Loaded += (_, _) => UiMotion.PopIn(this);
        _working = initial.Clone();
        _originalSnapshot = ThemeService.Resolve(App.Services.Config.Current.Ui ?? new UiPreferences()).Clone();
        BuildFields();
        ApplyLabels();
        LiveApply();
    }

    private void ApplyLabels()
    {
        TxtTitle.Text = Loc.Locale == "ru" ? "Своя палитра" : "Custom palette";
        LblPreview.Text = Loc.Locale == "ru" ? "Превью" : "Preview";
        BtnReset.Content = Loc.Locale == "ru" ? "Сброс" : "Reset defaults";
        BtnCancel.Content = Loc.T("btn.cancel");
        BtnOk.Content = Loc.T("btn.ok");
    }

    private void BuildFields()
    {
        PaletteHost.Children.Clear();
        _boxes.Clear();
        bool ru = Loc.Locale == "ru";
        foreach (var f in Fields)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            var swatch = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(6),
                BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(swatch, Dock.Left);

            var box = new System.Windows.Controls.TextBox
            {
                Width = 100,
                Text = f.Get(_working),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            };
            DockPanel.SetDock(box, Dock.Right);
            _boxes[f.Key] = box;

            var label = new TextBlock
            {
                Text = ru ? f.Ru : f.En,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 8, 0)
            };

            void SyncSwatch()
            {
                try
                {
                    var c = ThemeService.Hex(box.Text);
                    swatch.Background = new SolidColorBrush(c);
                }
                catch { }
            }

            SyncSwatch();
            box.TextChanged += (_, _) =>
            {
                SyncSwatch();
                f.Set(_working, box.Text.Trim());
                LiveApply();
            };

            row.Children.Add(swatch);
            row.Children.Add(box);
            row.Children.Add(label);
            PaletteHost.Children.Add(row);
        }
    }

    private void LiveApply()
    {
        ReadBoxesIntoWorking();
        ThemeService.ApplyPalette(_working);
    }

    private void ReadBoxesIntoWorking()
    {
        foreach (var f in Fields)
        {
            if (_boxes.TryGetValue(f.Key, out var box))
                f.Set(_working, box.Text.Trim());
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _working = ThemeService.SeedCustom("#C45C84", "#120E11");
        foreach (var f in Fields)
        {
            if (_boxes.TryGetValue(f.Key, out var box))
                box.Text = f.Get(_working);
        }
        LiveApply();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ReadBoxesIntoWorking();
        ResultPalette = _working.Clone();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.ApplyPalette(_originalSnapshot);
        DialogResult = false;
        Close();
    }
}
