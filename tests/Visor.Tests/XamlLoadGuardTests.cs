using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Visor.Tests;

/// <summary>
/// An element declared with IsChecked/IsSelected/IsExpanded="True" (or SelectedIndex) AND a change handler
/// fires that handler while InitializeComponent is still parsing — before elements declared later in the
/// file exist. The 2.1.0-dev startup crash (Nav_Checked → MainTabs null) was exactly this. Every such
/// handler must bail out early: a null check on a named element, a suppression flag, or IsInitialized.
/// Static check only — no window is created.
/// </summary>
public class XamlLoadGuardTests
{
    private static readonly Regex Element = new(@"<[\w:.]+\b[^>]*?>", RegexOptions.Singleline);
    private static readonly Regex InitialState = new(@"\b(IsChecked|IsSelected|IsExpanded)=""True""|\bSelectedIndex=""\d+""");
    private static readonly Regex Handler = new(@"\b(Checked|Selected|Expanded|SelectionChanged|Click)=""(\w+)""");

    // First statements of a guarded handler look like one of these.
    private static readonly Regex Guard = new(
        @"==\s*null\b[^;]*\)\s*return|is\s+null\b[^;]*\)\s*return|_suppress\w*\)\s*return|Suppress\w*\)\s*return|!IsInitialized|!IsLoaded",
        RegexOptions.Singleline);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Visor.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Visor.sln not found above the test output folder.");
    }

    public static IEnumerable<object[]> InitialStateHandlers()
    {
        string root = RepoRoot();
        foreach (var xaml in Directory.EnumerateFiles(Path.Combine(root, "Views"), "*.xaml", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "Controls"), "*.xaml", SearchOption.AllDirectories)))
        {
            string text = File.ReadAllText(xaml);
            foreach (Match el in Element.Matches(text))
            {
                if (!InitialState.IsMatch(el.Value)) continue;
                var h = Handler.Match(el.Value);
                if (!h.Success || h.Groups[1].Value == "Click") continue;
                yield return new object[] { Path.GetRelativePath(root, xaml), h.Groups[2].Value };
            }
        }
    }

    [Theory]
    [MemberData(nameof(InitialStateHandlers))]
    public void Handler_fired_during_xaml_load_is_guarded(string xamlFile, string handler)
    {
        string root = RepoRoot();
        string dir = Path.GetDirectoryName(Path.Combine(root, xamlFile))!;
        string stem = Path.GetFileNameWithoutExtension(xamlFile);
        var sources = Directory.EnumerateFiles(dir, stem + "*.cs").Select(File.ReadAllText).ToList();
        Assert.NotEmpty(sources);

        var decl = new Regex(@"void\s+" + Regex.Escape(handler) + @"\s*\([^)]*\)\s*\{(?<body>.{0,400})", RegexOptions.Singleline);
        var m = sources.Select(s => decl.Match(s)).FirstOrDefault(x => x.Success);
        Assert.True(m != null, $"{handler} not found next to {xamlFile}");

        // Guard inside the handler, or the window/page sets a suppression flag around InitializeComponent
        // that this handler checks.
        bool guarded = Guard.IsMatch(m!.Groups["body"].Value);
        Assert.True(guarded,
            $"{xamlFile}: {handler} runs during InitializeComponent (initial state in XAML) but has no early-return guard. " +
            "Add a null check on a later-declared element or a suppression flag set before InitializeComponent.");
    }

    [Fact]
    public void Scan_finds_the_main_window_nav_handler()
    {
        // Keeps the scan itself honest: if the regexes stop matching, the theory above silently runs zero cases.
        Assert.Contains(InitialStateHandlers(), row => (string)row[1] == "Nav_Checked");
    }

    [Fact]
    public void Main_window_suppresses_nav_sync_during_initialize_component()
    {
        string cs = File.ReadAllText(Path.Combine(RepoRoot(), "Views", "MainWindow.xaml.cs"));
        int suppress = cs.IndexOf("_suppressNav = true;", StringComparison.Ordinal);
        int init = cs.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        Assert.True(suppress >= 0 && init > suppress, "_suppressNav must be set before InitializeComponent in the MainWindow constructor.");
    }
}
