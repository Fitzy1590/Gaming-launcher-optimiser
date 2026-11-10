using System.Collections.Concurrent;
using FitzLauncher.Core.Fps;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace FitzLauncher.Windows;

/// <summary>
/// Low-overhead FPS capture using the same ETW events PresentMon uses: no injection, no overlay,
/// nothing loaded into the game (so it's anti-cheat safe). The cost is a few hundred tiny events per
/// second on a background thread, and frame times go straight into a fixed-size histogram.
///
/// Sources: DXGI Present_Start (D3D10/11/12), D3D9 Present_Start, and DxgKrnl Present_Info as a
/// fallback for Vulkan/OpenGL, which bypass DXGI. Needs admin (or "Performance Log Users").
/// </summary>
public sealed class EtwFpsTracker : IDisposable
{
    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnl = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;
    private const int DxgKrnlPresentInfo = 184;
    private const ulong DxgiD3D9Keyword = 0x2;
    private const ulong DxgKrnlPresentKeyword = 0x8000000;
    private const string SessionName = "FitzLauncher-FPS";

    private sealed class PidState
    {
        public readonly FrameTimeRecorder Recorder = new();
        public double LastApiPresentMs = double.NaN;
        public double LastKernelPresentMs = double.NaN;
        public double LastApiSeenMs = double.NegativeInfinity;
    }

    private readonly ConcurrentDictionary<int, PidState> _pids = new();
    private volatile Func<int, bool> _isGameProcess = _ => false;
    /// <summary>Extra recorder fed with the same frames (benchmark capture), or null.</summary>
    private volatile FrameTimeRecorder? _sink;

    public void SetSink(FrameTimeRecorder? sink) => _sink = sink;
    private TraceEventSession? _session;
    private Thread? _thread;

    public string? Error { get; private set; }

    public bool Start(Func<int, bool> isGameProcess)
    {
        _isGameProcess = isGameProcess;
        try
        {
            if (!(TraceEventSession.IsElevated() ?? false))
            {
                Error = "FPS tracking needs the launcher to run as administrator.";
                return false;
            }
            // Clean up a session left behind by a crash.
            if (TraceEventSession.GetActiveSessionNames().Contains(SessionName))
                using (var stale = new TraceEventSession(SessionName)) stale.Stop();

            _session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 8 };
            _session.EnableProvider(Dxgi, TraceEventLevel.Informational, DxgiD3D9Keyword);
            _session.EnableProvider(D3D9, TraceEventLevel.Informational, DxgiD3D9Keyword);
            _session.EnableProvider(DxgKrnl, TraceEventLevel.Informational, DxgKrnlPresentKeyword);
            _session.Source.AllEvents += OnEvent;

            _thread = new Thread(() =>
            {
                try { _session.Source.Process(); }
                catch (Exception e) { Error = e.Message; }
            }) { IsBackground = true, Name = "FPS capture", Priority = ThreadPriority.BelowNormal };
            _thread.Start();
            return true;
        }
        catch (Exception e)
        {
            Error = "Could not start FPS capture: " + e.Message;
            _session?.Dispose();
            _session = null;
            return false;
        }
    }

    private long _testPresents;

    /// <summary>How many DXGI test presents were left out of the frame count (for the log).</summary>
    public long TestPresentsIgnored => Interlocked.Read(ref _testPresents);

    private const uint DxgiPresentTest = 0x1; // DXGI_PRESENT_TEST (dxgi.h)

    private static bool IsTestPresent(TraceEvent e)
    {
        try { return e.PayloadByName("Flags") is { } f && (Convert.ToUInt32(f, System.Globalization.CultureInfo.InvariantCulture) & DxgiPresentTest) != 0; }
        catch (Exception ex) when (ex is IndexOutOfRangeException or InvalidCastException or FormatException or OverflowException) { return false; }
    }

    private void OnEvent(TraceEvent e)
    {
        var pid = e.ProcessID;
        var id = (int)e.ID;
        var provider = e.ProviderGuid;
        bool api = (provider == Dxgi && id == DxgiPresentStart) || (provider == D3D9 && id == D3D9PresentStart);
        bool kernel = provider == DxgKrnl && id == DxgKrnlPresentInfo;
        if (!api && !kernel) return;
        if (!_isGameProcess(pid)) return;
        // Present(…, DXGI_PRESENT_TEST) only asks whether the window is visible: nothing is drawn, so it isn't a frame
        // (PresentMon skips these too). Counting them made FPS read about 10 % high in some games.
        if (api && provider == Dxgi && IsTestPresent(e)) { Interlocked.Increment(ref _testPresents); return; }

        var state = _pids.GetOrAdd(pid, _ => new PidState());
        var t = e.TimeStampRelativeMSec;
        if (api)
        {
            if (!double.IsNaN(state.LastApiPresentMs)) Add(state, t - state.LastApiPresentMs);
            state.LastApiPresentMs = t;
            state.LastApiSeenMs = t;
        }
        else
        {
            // Kernel present events also fire for DXGI apps; only use them when the API events are absent.
            if (t - state.LastApiSeenMs < 2000) return;
            if (!double.IsNaN(state.LastKernelPresentMs)) Add(state, t - state.LastKernelPresentMs);
            state.LastKernelPresentMs = t;
        }
    }

    private void Add(PidState state, double frameMs)
    {
        state.Recorder.AddFrame(frameMs);
        // Only the main rendering process feeds the benchmark (launchers/overlays present too).
        if (_sink is { } sink && ReferenceEquals(state.Recorder, PrimaryRecorder)) sink.AddFrame(frameMs);
    }

    /// <summary>The recorder for the process that presented the most frames (the actual game).</summary>
    public FrameTimeRecorder? PrimaryRecorder =>
        _pids.Values.OrderByDescending(s => s.Recorder.FrameCount).FirstOrDefault()?.Recorder;

    public double CurrentFps => PrimaryRecorder?.CurrentFps ?? 0;

    public void Stop()
    {
        try { _session?.Dispose(); } catch (Exception) { /* already gone */ }
        _session = null;
        _thread?.Join(2000);
        _thread = null;
    }

    public void Dispose() => Stop();
}
