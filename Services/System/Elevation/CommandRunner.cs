using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Visor.Services.Elevation;

/// <summary>Exit code (null = timed out / could not start) and captured stdout+stderr.</summary>
public sealed record CommandResult(int? ExitCode, string Output)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Seam for console tools (netsh, powercfg). Tests inject canned output; nothing real is run.</summary>
public interface ICommandRunner
{
    /// <param name="systemTool">File name inside %SystemRoot%\System32 (e.g. "netsh.exe") — never a search-path lookup.</param>
    CommandResult Run(string systemTool, IReadOnlyList<string> args, TimeSpan timeout);
}

/// <summary>
/// Runs a System32 console tool hidden, with an argument list (no shell, no string concatenation)
/// and a hard timeout. Output is decoded with the OEM code page — that is what localized
/// netsh / powercfg write into a pipe (cp866 on Russian Windows, cp437 on English).
/// </summary>
public sealed class SystemCommandRunner : ICommandRunner
{
    public static readonly SystemCommandRunner Instance = new();

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static Encoding? _oem;

    private static Encoding OemEncoding
    {
        get
        {
            if (_oem != null) return _oem;
            Encoding enc;
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                enc = Encoding.GetEncoding((int)GetOEMCP());
            }
            catch (Exception)
            {
                enc = Encoding.UTF8;
            }
            return _oem = enc;
        }
    }

    public CommandResult Run(string systemTool, IReadOnlyList<string> args, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrEmpty(systemTool) || systemTool.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new ArgumentException("System32 tool file name expected", nameof(systemTool));

        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, systemTool))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = OemEncoding,
            StandardErrorEncoding = OemEncoding,
            WorkingDirectory = Environment.SystemDirectory
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var sb = new StringBuilder();
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (gate) sb.AppendLine(e.Data); };
        try
        {
            if (!p.Start()) return new CommandResult(null, "");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new CommandResult(null, ex.Message);
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 100, int.MaxValue)))
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            lock (gate) return new CommandResult(null, sb.ToString());
        }
        p.WaitForExit(); // flush async readers
        lock (gate) return new CommandResult(p.ExitCode, sb.ToString());
    }
}
