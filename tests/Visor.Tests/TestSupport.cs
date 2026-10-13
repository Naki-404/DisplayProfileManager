using System.IO;
using System.Runtime.CompilerServices;
using Visor.Models;
using Visor.Services;

namespace Visor.Tests;

/// <summary>
/// Hermetic test environment: the log goes to a temp folder (never %AppData%\Visor), and the
/// "factory defaults" capture is replaced by a fixed value so no display / power API is read.
/// </summary>
internal static class TestEnvironment
{
    public static readonly string Root = Path.Combine(Path.GetTempPath(), "VisorTests");

    public const string FakeResolution = "1920x1080";
    public const string FakePowerPlan = "highPerformance";

    [ModuleInitializer]
    internal static void Init()
    {
        Directory.CreateDirectory(Root);
        AppLog.Configure(Path.Combine(Root, "Visor.Tests.log"));
        ConfigService.FactoryDefaultsProvider = () => new DefaultSettings
        {
            Resolution = FakeResolution,
            PowerPlan = FakePowerPlan,
            Color = ColorSettings.Neutral
        };
    }
}

/// <summary>A unique temp directory under %TEMP%\VisorTests, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(TestEnvironment.Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, string content)
    {
        var p = File(name);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}
