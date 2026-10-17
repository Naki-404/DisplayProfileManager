using System.ComponentModel;
using System.Diagnostics;

namespace Visor.Services.FrameCapture;

/// <summary>Process-launch seam (the real one calls Process.Start with the "runas" verb).</summary>
internal interface IProcessLauncher
{
    /// <summary>Throws <see cref="Win32Exception"/> (1223 = UAC declined) or other exceptions on failure.</summary>
    IProcessHandle Launch(ProcessStartInfo psi);
}

internal interface IProcessHandle : IDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    /// <summary>Raised once when the process exits (any thread).</summary>
    event Action? Exited;
}

/// <summary>ShellExecute launcher: <c>Process.Start</c> returns once UAC consent was given and the process exists.</summary>
internal sealed class ShellProcessLauncher : IProcessLauncher
{
    public IProcessHandle Launch(ProcessStartInfo psi)
    {
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        return new Handle(p);
    }

    private sealed class Handle : IProcessHandle
    {
        private readonly Process _p;
        private int _raised;

        public Handle(Process p)
        {
            _p = p;
            try
            {
                _p.EnableRaisingEvents = true;
                _p.Exited += (_, _) => Raise();
                if (_p.HasExited) Raise();
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                AppLog.Warn(ex, "Frame capture: exit tracking");
            }
        }

        private void Raise()
        {
            if (Interlocked.Exchange(ref _raised, 1) == 0) Exited?.Invoke();
        }

        public bool HasExited { get { try { return _p.HasExited; } catch { return true; } } }
        public int? ExitCode { get { try { return _p.HasExited ? _p.ExitCode : null; } catch { return null; } } }
        public event Action? Exited;
        public void Dispose() => _p.Dispose();
    }
}
