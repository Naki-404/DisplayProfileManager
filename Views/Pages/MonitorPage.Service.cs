using Visor.Services;

namespace Visor;

public partial class MonitorPage
{
    /// <summary>
    /// The shared frame-capture facade used by this page, the Settings "Monitor" group and the FPS overlay
    /// (<see cref="AppServices.FrameMonitor"/>; one capture at a time, never auto-started).
    /// </summary>
    internal static MonitorService FrameMonitor => App.Services.FrameMonitor;
}
