namespace Visor.Services.FrameCapture;

/// <summary>Which ETW provider reported a present. Lower value = preferred when one process shows up in several.</summary>
public enum PresentSource : byte
{
    Unknown = 0,
    /// <summary>Microsoft-Windows-DXGI (D3D10/11/12 swap chains, and Vulkan/OpenGL layered on DXGI).</summary>
    Dxgi = 1,
    /// <summary>Microsoft-Windows-D3D9.</summary>
    D3d9 = 2,
    /// <summary>Microsoft-Windows-DxgKrnl (kernel present paths: native Vulkan / OpenGL / anything bypassing DXGI).</summary>
    DxgKrnl = 3
}

/// <summary>
/// One decoded present-start event. <see cref="Qpc"/> is the raw QueryPerformanceCounter
/// timestamp from <c>EVENT_HEADER.TimeStamp</c>; <see cref="SwapChain"/> is the swap-chain
/// pointer from the DXGI / D3D9 payload (0 when unknown — DxgKrnl events are tracked per process).
/// </summary>
public readonly record struct PresentEvent(long Qpc, int ProcessId, PresentSource Source, ushort EventId, ulong SwapChain = 0);

/// <summary>
/// Provider GUIDs, event IDs and keywords. Values verified against the providers' published manifests
/// (Microsoft-Windows-DXGI, -D3D9, -DxgKrnl) — see docs\MONITOR-NATIVE.md for the confidence of each
/// and the fallback plan.
/// </summary>
public static class PresentProviders
{
    public static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    public static readonly Guid D3d9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    public static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    /// <summary>DXGI Present_Start (task 9, opcode Start). Payload: pIDXGISwapChain, Flags, SyncInterval.</summary>
    public const ushort DxgiPresentStart = 42;
    /// <summary>DXGI PresentMultiplaneOverlay_Start (task 14). Same payload prefix as Present_Start.</summary>
    public const ushort DxgiPresentMpoStart = 55;
    /// <summary>D3D9 Present_Start. Payload: pSwapchain, Flags.</summary>
    public const ushort D3d9PresentStart = 1;

    /// <summary>DxgKrnl Present_Info (D3DKMTPresent; blt and legacy flip presents).</summary>
    public const ushort DxgkPresentInfo = 184;
    /// <summary>DxgKrnl PresentHistoryDetailed_Start (token-based flip / composition presents).</summary>
    public const ushort DxgkPresentHistoryDetailedStart = 215;
    /// <summary>DxgKrnl PresentHistory_Start (same role as the detailed variant, older / non-display-tracking form).</summary>
    public const ushort DxgkPresentHistoryStart = 171;
    /// <summary>DxgKrnl Flip_Info (fullscreen flip submitted by the presenting thread).</summary>
    public const ushort DxgkFlipInfo = 168;
    /// <summary>DxgKrnl Blit_Info (windowed blt present).</summary>
    public const ushort DxgkBlitInfo = 166;
    /// <summary>DxgKrnl FlipMultiPlaneOverlay_Info (one per plane — least preferred).</summary>
    public const ushort DxgkFlipMpoInfo = 252;

    /// <summary>DXGI "Events" keyword (Present_* events carry 0x8000000000000002).</summary>
    public const ulong DxgiKeywords = 0x2;
    /// <summary>D3D9 "Events" keyword.</summary>
    public const ulong D3d9Keywords = 0x2;
    /// <summary>DxgKrnl "Present" keyword (0x8000000); the present events carry 0x4000000008000001. Not "Base" (0x1), which also matches the heavy DMA / allocation events.</summary>
    public const ulong DxgKrnlKeywords = 0x8000000;

    public static readonly ushort[] DxgiEventIds = { DxgiPresentStart, DxgiPresentMpoStart };
    public static readonly ushort[] D3d9EventIds = { D3d9PresentStart };
    public static readonly ushort[] DxgKrnlEventIds =
    {
        DxgkPresentInfo, DxgkPresentHistoryDetailedStart, DxgkPresentHistoryStart, DxgkFlipInfo, DxgkBlitInfo, DxgkFlipMpoInfo
    };

    /// <summary>Source of a (provider, event id) pair we subscribe to, or Unknown for anything else.</summary>
    public static PresentSource Classify(Guid provider, ushort eventId)
    {
        if (provider == Dxgi) return eventId is DxgiPresentStart or DxgiPresentMpoStart ? PresentSource.Dxgi : PresentSource.Unknown;
        if (provider == D3d9) return eventId == D3d9PresentStart ? PresentSource.D3d9 : PresentSource.Unknown;
        if (provider == DxgKrnl) return Array.IndexOf(DxgKrnlEventIds, eventId) >= 0 ? PresentSource.DxgKrnl : PresentSource.Unknown;
        return PresentSource.Unknown;
    }
}

/// <summary>What the event source should listen to.</summary>
/// <param name="ProcessIds">Target processes (callback-side filter always; provider-side PID filter when ≤ 8).</param>
public sealed record PresentEventFilter(IReadOnlyCollection<int> ProcessIds);

/// <summary>Seam over the real-time ETW session (tests use a scripted fake; nothing else implements it).</summary>
public interface IPresentEventSource : IDisposable
{
    /// <summary>Ticks per second of <see cref="PresentEvent.Qpc"/>.</summary>
    long QpcFrequency { get; }

    /// <summary>
    /// Starts the session and the consumer thread. Returns 0 on success or a Win32 error code
    /// (5 = access denied / not elevated, 1450 = no system resources, …). <paramref name="onEvent"/>
    /// runs on the consumer thread and must be quick.
    /// </summary>
    uint Start(PresentEventFilter filter, Action<PresentEvent> onEvent);

    /// <summary>Completes when the consumer thread ends (after <see cref="Stop"/>, or when the session is killed from outside).</summary>
    Task Completion { get; }

    /// <summary>Human-readable summary of how each provider was enabled (filters applied / fallbacks).</summary>
    string EnableSummary { get; }

    /// <summary>Stops the ETW session (ControlTrace STOP) and joins the consumer. Idempotent, never throws.</summary>
    void Stop();
}
