using System.Windows;
using DisplayProfileManager.Services;

namespace DisplayProfileManager;

public partial class PackGalleryWindow : Window
{
    public PackGalleryWindow()
    {
        InitializeComponent();
        Opacity = 0;
        Loaded += (_, _) => UiMotion.PopIn(this);
        ApplyLabels();
    }

    private void ApplyLabels()
    {
        Title = Loc.T("presets.gallery");
        TxtTitle.Text = Loc.T("presets.gallery");
        LblHint.Text = Loc.T("presets.gallery.hint");
        LblTarkov.Text = Loc.T("pack.tarkov");
        LblTarkovSub.Text = Loc.T("pack.tarkov.sub");
        LblValorant.Text = Loc.T("pack.valorant");
        LblValorantSub.Text = Loc.T("pack.valorant.sub");
        LblPubg.Text = Loc.T("pack.pubg");
        LblPubgSub.Text = Loc.T("pack.pubg.sub");
        BtnTarkov.Content = Loc.T("btn.apply");
        BtnValorant.Content = Loc.T("btn.apply");
        BtnPubg.Content = Loc.T("btn.apply");
        BtnClose.Content = Loc.T("btn.cancel");
    }

    private void ApplyTarkov_Click(object sender, RoutedEventArgs e)
        => Apply(GameCatalog.CreateTarkovRivaPresets(), Loc.T("pack.tarkov"));

    private void ApplyValorant_Click(object sender, RoutedEventArgs e)
        => Apply(GameCatalog.CreateValorantStretchPresets(), Loc.T("pack.valorant"));

    private void ApplyPubg_Click(object sender, RoutedEventArgs e)
        => Apply(GameCatalog.CreatePubgStretchPresets(), Loc.T("pack.pubg"));

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
