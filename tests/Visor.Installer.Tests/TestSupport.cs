using System;
using System.IO;
using System.Threading;

namespace Visor.Installer.Tests;

/// <summary>A unique temp directory under %TEMP%\VisorInstallerTests, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public static readonly string Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VisorInstallerTests");

    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N"));
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
