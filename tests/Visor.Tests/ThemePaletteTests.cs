using System.IO;
using Visor.Models;
using Visor.Services;
using Xunit;

namespace Visor.Tests;

public class ThemePaletteTests
{
    [Fact]
    public void DarkPalette_includes_success_and_warning()
    {
        var p = ThemeService.DarkPalette();
        Assert.False(string.IsNullOrWhiteSpace(p.Success));
        Assert.False(string.IsNullOrWhiteSpace(p.Warning));
        Assert.StartsWith("#", p.Success);
        Assert.StartsWith("#", p.Warning);
    }

    [Fact]
    public void ThemePalette_Clone_copies_success_and_warning()
    {
        var original = ThemeService.DarkPalette();
        original.Success = "#11AA22";
        original.Warning = "#BBAA00";

        var clone = original.Clone();

        Assert.Equal("#11AA22", clone.Success);
        Assert.Equal("#BBAA00", clone.Warning);
        Assert.NotSame(original, clone);
    }

    [Fact]
    public void SeedCustom_fills_success_and_warning_for_dark_bg()
    {
        var p = ThemeService.SeedCustom("#22D3EE", "#0A0C10");
        Assert.Equal("#22C55E", p.Success);
        Assert.Equal("#F59E0B", p.Warning);
    }

    [Fact]
    public void SeedCustom_uses_darker_status_accents_on_light_bg()
    {
        var p = ThemeService.SeedCustom("#0E7490", "#F3F6FA");
        Assert.Equal("#15803D", p.Success);
        Assert.Equal("#B45309", p.Warning);
    }

    [Fact]
    public void SeedCustom_uses_the_deeper_danger_red_on_light_bg()
    {
        Assert.Equal("#E05252", ThemeService.SeedCustom("#22D3EE", "#0A0C10").Danger);
        Assert.Equal(ThemeService.LightPalette().Danger, ThemeService.SeedCustom("#0E7490", "#F3F6FA").Danger);
    }

    [Fact]
    public void LightPalette_uses_darker_status_accents_for_contrast()
    {
        var p = ThemeService.LightPalette();
        // Light theme uses deeper greens/ambers so status text stays readable on white panels.
        Assert.Equal("#15803D", p.Success);
        Assert.Equal("#B45309", p.Warning);
    }

    [Fact]
    public void ThemePalette_defaults_match_noir_brand()
    {
        var p = new ThemePalette();
        Assert.Equal("#0A0C10", p.Bg);
        Assert.Equal("#22D3EE", p.Accent);
        Assert.Equal("#22C55E", p.Success);
        Assert.Equal("#F59E0B", p.Warning);
    }

    [Fact]
    public void BestContrastingLabel_prefers_dark_ink_on_bright_success_green()
    {
        var label = ThemeService.BestContrastingLabel(ThemeService.Hex("#22C55E"));
        Assert.Equal("#052E16", ThemeService.ToHex(label));
    }

    [Fact]
    public void BestContrastingLabel_prefers_white_on_deep_success_green()
    {
        var label = ThemeService.BestContrastingLabel(ThemeService.Hex("#15803D"));
        Assert.Equal("#FFFFFF", ThemeService.ToHex(label));
    }

    // -- Stage 1 shell: card tokens + AA contrast on card surfaces ------------------------------------

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Text_and_muted_text_meet_AA_on_panel_and_background(string theme)
    {
        var p = theme == "light" ? ThemeService.LightPalette() : ThemeService.DarkPalette();
        var panel = ThemeService.Hex(p.Panel);
        var bg = ThemeService.Hex(p.Bg);
        var text = ThemeService.Hex(p.Text);
        var muted = ThemeService.Hex(p.Muted);
        Assert.True(ThemeService.ContrastRatio(text, panel) >= 4.5, $"{theme}: text on panel");
        Assert.True(ThemeService.ContrastRatio(muted, panel) >= 4.5, $"{theme}: muted on panel");
        Assert.True(ThemeService.ContrastRatio(text, bg) >= 4.5, $"{theme}: text on bg");
        Assert.True(ThemeService.ContrastRatio(muted, bg) >= 4.5, $"{theme}: muted on bg");
    }

    [Theory]
    [InlineData("dark")]
    [InlineData("light")]
    public void Status_colors_are_distinguishable_from_card_surface(string theme)
    {
        var p = theme == "light" ? ThemeService.LightPalette() : ThemeService.DarkPalette();
        var panel = ThemeService.Hex(p.Panel);
        foreach (var hex in new[] { p.Success, p.Warning, p.Danger })
            Assert.True(ThemeService.ContrastRatio(ThemeService.Hex(hex), panel) >= 3.0, $"{theme}: {hex} on panel");
    }

    [Fact]
    public void CardColors_hover_border_leans_toward_accent_and_differs_from_rest()
    {
        var p = ThemeService.DarkPalette();
        var (border, hover) = ThemeService.CardColors(p);
        Assert.Equal(p.Border, ThemeService.ToHex(border));
        Assert.NotEqual(ThemeService.ToHex(border), ThemeService.ToHex(hover));
        // Hover sits between the rest border and the accent (cyan): more blue/green than the rest border.
        var accent = ThemeService.Hex(p.Accent);
        Assert.True(hover.B >= border.B && hover.B <= Math.Max(border.B, accent.B));
        Assert.True(hover.G >= border.G && hover.G <= Math.Max(border.G, accent.G));
    }

    [Fact]
    public void ContrastRatio_is_symmetric_and_bounded()
    {
        var a = ThemeService.Hex("#000000");
        var b = ThemeService.Hex("#FFFFFF");
        Assert.Equal(ThemeService.ContrastRatio(a, b), ThemeService.ContrastRatio(b, a), 6);
        Assert.Equal(21.0, ThemeService.ContrastRatio(a, b), 1);
        Assert.Equal(1.0, ThemeService.ContrastRatio(a, a), 6);
    }
}

/// <summary>
/// App.xaml resources are only resolved at run time (StaticResource, BasedOn, template parts), so the build can't
/// catch a broken menu / dropdown / scrollbar style. These tests load the &lt;Application.Resources&gt; block as a plain
/// ResourceDictionary on an STA thread (no App, no Window) and instantiate the shared popup / scroll templates.
/// </summary>
public class AppResourceDictionaryTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Visor.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, relative);
    }

    private static System.Windows.ResourceDictionary LoadAppResources()
    {
        string xaml = File.ReadAllText(RepoFile("App.xaml"));
        const string open = "<Application.Resources>";
        int a = xaml.IndexOf(open, StringComparison.Ordinal);
        int b = xaml.IndexOf("</Application.Resources>", StringComparison.Ordinal);
        Assert.True(a > 0 && b > a);
        string inner = xaml.Substring(a + open.Length, b - a - open.Length);
        string dict =
            "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
            "xmlns:local=\"clr-namespace:Visor;assembly=Visor\" " +
            "xmlns:controls=\"clr-namespace:Visor.Controls;assembly=Visor\">" + inner + "</ResourceDictionary>";
        return (System.Windows.ResourceDictionary)System.Windows.Markup.XamlReader.Parse(dict);
    }

    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Layout(System.Windows.FrameworkElement e, double w = 400, double h = 300)
    {
        e.Measure(new System.Windows.Size(w, h));
        e.Arrange(new System.Windows.Rect(0, 0, w, h));
        e.UpdateLayout();
    }

    [Fact]
    public void Every_app_resource_materializes() => RunSta(() =>
    {
        var rd = LoadAppResources();
        foreach (var key in rd.Keys.Cast<object>().ToList())
            Assert.NotNull(rd[key]);
    });

    [Fact]
    public void ContextMenu_and_MenuItem_templates_have_no_icon_gutter_and_32_dip_rows() => RunSta(() =>
    {
        var rd = LoadAppResources();
        var menu = new System.Windows.Controls.ContextMenu();
        menu.Resources.MergedDictionaries.Add(rd);
        var plain = new System.Windows.Controls.MenuItem { Header = "Duplicate" };
        var gesture = new System.Windows.Controls.MenuItem { Header = "Export", InputGestureText = "Ctrl+E" };
        var sub = new System.Windows.Controls.MenuItem { Header = "More" };
        sub.Items.Add(new System.Windows.Controls.MenuItem { Header = "Child" });
        menu.Items.Add(plain);
        menu.Items.Add(gesture);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(sub);
        Layout(menu);

        Assert.NotNull(menu.Template);
        foreach (var mi in new[] { plain, gesture, sub })
        {
            Assert.NotNull(mi.Template);
            Assert.True(mi.ActualHeight >= 32 - 0.01, $"menu row height {mi.ActualHeight}");
            var icon = (System.Windows.FrameworkElement)mi.Template.FindName("IconHost", mi);
            Assert.Equal(System.Windows.Visibility.Collapsed, icon.Visibility); // no empty gutter
        }
        var arrow = (System.Windows.UIElement)sub.Template.FindName("Arrow", sub);
        Assert.Equal(System.Windows.Visibility.Visible, arrow.Visibility);
        Assert.Equal(plain.ActualHeight, gesture.ActualHeight, 2);
    });

    [Fact]
    public void ComboBox_dropdown_and_items_use_shared_popup_chrome() => RunSta(() =>
    {
        var rd = LoadAppResources();
        var combo = new System.Windows.Controls.ComboBox { Width = 200 };
        combo.Resources.MergedDictionaries.Add(rd);
        combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Dark" });
        combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = "Light" });
        combo.SelectedIndex = 0;
        Layout(combo);
        Assert.NotNull(combo.Template.FindName("Popup", combo));
        Assert.True(combo.ActualHeight >= 34 - 0.01);

        var editable = new System.Windows.Controls.ComboBox { Width = 200, IsEditable = true };
        editable.Resources.MergedDictionaries.Add(rd);
        Layout(editable);
        // Editable and read-only combos must be the same height (inner TextBox MinHeight reset to 0).
        Assert.Equal(combo.ActualHeight, editable.ActualHeight, 1);
    });

    [Fact]
    public void ScrollViewer_bars_overlay_content_and_start_hidden() => RunSta(() =>
    {
        var rd = LoadAppResources();
        var sv = new System.Windows.Controls.ScrollViewer
        {
            Content = new System.Windows.Controls.Border { Height = 2000 }
        };
        sv.Resources.MergedDictionaries.Add(rd);
        Layout(sv, 300, 200);
        Assert.True(Visor.Controls.ScrollIndicator.GetAutoHide(sv));
        var bar = (System.Windows.Controls.Primitives.ScrollBar)sv.Template.FindName("PART_VerticalScrollBar", sv);
        Assert.Equal(System.Windows.Visibility.Visible, bar.Visibility); // content overflows → bar exists
        Assert.Equal(0.0, bar.Opacity);                                     // …but is invisible at rest
        // Overlay: the content gets the full viewport width (no column reserved for the bar).
        Assert.Equal(300, sv.ViewportWidth, 1);

        var fits = new System.Windows.Controls.ScrollViewer { Content = new System.Windows.Controls.Border { Height = 50 } };
        fits.Resources.MergedDictionaries.Add(rd);
        Layout(fits, 300, 200);
        var bar2 = (System.Windows.Controls.Primitives.ScrollBar)fits.Template.FindName("PART_VerticalScrollBar", fits);
        Assert.Equal(System.Windows.Visibility.Collapsed, bar2.Visibility); // content fits → no bar at all
    });

    [Fact]
    public void ListBox_padding_reaches_the_inner_scroller() => RunSta(() =>
    {
        // GamesPage tile list relies on ListBox.Padding → ScrollViewer.Padding to keep the overlay bar off the tiles.
        var rd = LoadAppResources();
        var list = new System.Windows.Controls.ListBox { Padding = new System.Windows.Thickness(0, 0, 10, 0) };
        list.Resources.MergedDictionaries.Add(rd);
        // App.xaml keeps the theme (Aero2) ListBox template; a test host has no theme lookup, so take it explicitly.
        var aero2 = new System.Windows.ResourceDictionary
        {
            Source = new Uri("/PresentationFramework.Aero2;component/themes/aero2.normalcolor.xaml", UriKind.Relative)
        };
        var themeStyle = (System.Windows.Style)aero2[typeof(System.Windows.Controls.ListBox)];
        list.Template = themeStyle.Setters.OfType<System.Windows.Setter>()
            .Select(s => s.Value).OfType<System.Windows.Controls.ControlTemplate>().First();
        for (int i = 0; i < 30; i++) list.Items.Add("Item " + i);
        Layout(list, 260, 200);
        System.Windows.Controls.ScrollViewer? Find(System.Windows.DependencyObject d)
        {
            if (d is System.Windows.Controls.ScrollViewer s) return s;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++)
                if (Find(System.Windows.Media.VisualTreeHelper.GetChild(d, i)) is { } hit) return hit;
            return null;
        }
        Assert.NotNull(list.Template);
        list.ApplyTemplate();
        var sv = Find(list);
        Assert.True(sv != null, "children=" + System.Windows.Media.VisualTreeHelper.GetChildrenCount(list)
            + " root=" + (System.Windows.Media.VisualTreeHelper.GetChildrenCount(list) > 0 ? System.Windows.Media.VisualTreeHelper.GetChild(list, 0).GetType().Name : "-"));
        Assert.Equal(10, sv!.Padding.Right, 1);
    });

    [Fact]
    public void Accent_and_ghost_buttons_have_equal_height() => RunSta(() =>
    {
        var rd = LoadAppResources();
        double H(string key)
        {
            var b = new System.Windows.Controls.Button { Content = "Apply", Style = (System.Windows.Style)rd[key] };
            b.Resources.MergedDictionaries.Add(rd);
            b.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            return b.DesiredSize.Height;
        }
        Assert.Equal(H("AccentButton"), H("GhostButton"), 1);
        Assert.Equal(H("CompactAccentButton"), H("CompactGhostButton"), 1);
        Assert.Equal(H("CompactGhostButton"), H("DangerGhostButton"), 1);
        Assert.Equal(H("ChipButton"), H("ChipButtonSelected"), 1);
    });
}
