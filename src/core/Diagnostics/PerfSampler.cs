
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Avalonia.Threading;

namespace backs2.Core.Diagnostics;

public sealed class PerfSampler : IDisposable
{
    public const double SustainSeconds = 2.0;
    public const double ReportCooldownSeconds = 30.0;

    private readonly PerfThresholds _thresholds;
    private readonly object _gate = new();

    private Timer? _timer;
    private Process? _process;
    private long _lastCpuTicks;
    private long _lastSampleTicks;
    private int _frames;
    private readonly Dictionary<string, int> _streak = new();
    private readonly Dictionary<string, DateTime> _lastReport = new();

    public PerfSampler(PerfThresholds? thresholds = null)
    {
        _thresholds = thresholds ?? new PerfThresholds();
    }

    public event Action<PerfSnapshot>? Sampled;
    public event Action<PerfSnapshot, IReadOnlyList<string>>? SustainedBreach;

    public Func<(double? UsedMb, double? BudgetMb, double? GpuPercent)>? DeviceProbe { get; set; }

    public bool IsRunning { get { lock (_gate) return _timer != null; } }

    public void OnFrame() => Interlocked.Increment(ref _frames);

    public void Start()
    {
        lock (_gate)
        {
            if (_timer != null) return;
            _process = Process.GetCurrentProcess();
            _lastCpuTicks = _process.TotalProcessorTime.Ticks;
            _lastSampleTicks = Stopwatch.GetTimestamp();
            _frames = 0;
            _streak.Clear();
            _lastReport.Clear();
            _timer = new Timer(_ => Tick(), null, 1000, 1000);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _process?.Dispose();
            _process = null;
        }
    }

    public void Dispose() => Stop();

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    /// <summary>
    /// Physical memory available to the OS, MB.
    ///
    /// <para>Not <c>GC.GetGCMemoryInfo().TotalAvailableMemoryBytes</c>, which
    /// reports the GC's heap budget — roughly total physical memory on a
    /// machine with no container limit. That value never falls, so a low-RAM
    /// threshold evaluated against it could never fire.</para>
    /// </summary>
    private static double QueryAvailableRamMb()
    {
        try
        {
            var st = new MemoryStatusEx();
            st.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MemoryStatusEx>();
            if (GlobalMemoryStatusEx(ref st))
                return st.ullAvailPhys / 1048576.0;
        }
        catch
        {
            // Non-Windows or a restricted host: report 0 so the metric reads
            // as unavailable rather than silently claiming plenty of memory.
        }

        return 0.0;
    }

    private void Tick()
    {
        PerfSnapshot snap;
        string[] breaches;

        lock (_gate)
        {
            if (_process == null) return;
            _process.Refresh();

            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
            if (dt <= 0) dt = 1.0;

            long cpu = _process.TotalProcessorTime.Ticks;
            double cpuSec = (cpu - _lastCpuTicks) / (double)TimeSpan.TicksPerSecond;
            double cpuPct = Math.Clamp(cpuSec / dt * 100.0, 0.0, 100.0 * Environment.ProcessorCount);
            _lastCpuTicks = cpu;
            _lastSampleTicks = now;

            int frames = Interlocked.Exchange(ref _frames, 0);
            double? fps = frames / dt;

            double wsMb = _process.WorkingSet64 / 1048576.0;
            double availMb = PerfSampler.QueryAvailableRamMb();

            double? vu = null, vb = null, gp = null;
            var probe = DeviceProbe;
            if (probe != null)
            {
                try { var d = probe(); vu = d.UsedMb; vb = d.BudgetMb; gp = d.GpuPercent; }
                catch { }
            }

            snap = new PerfSnapshot(DateTime.UtcNow, fps, wsMb, availMb, cpuPct, vu, vb, gp);
            breaches = _thresholds.Breaches(snap);
        }

        var fired = UpdateStreaks(snap, breaches);

        Dispatcher.UIThread.Post(() =>
        {
            Sampled?.Invoke(snap);
            if (fired.Count > 0) SustainedBreach?.Invoke(snap, fired);
        });
    }

    private IReadOnlyList<string> UpdateStreaks(PerfSnapshot snap, string[] breaches)
    {
        var fired = new List<string>(breaches.Length);

        lock (_gate)
        {
            foreach (var k in new List<string>(_streak.Keys))
                if (Array.IndexOf(breaches, k) < 0) _streak[k] = 0;

            foreach (var m in breaches)
            {
                int s = _streak.TryGetValue(m, out int prev) ? prev + 1 : 1;
                _streak[m] = s;

                if (s < (int)Math.Ceiling(SustainSeconds)) continue;

                if (_lastReport.TryGetValue(m, out var last) &&
                    (snap.Utc - last).TotalSeconds < ReportCooldownSeconds) continue;

                _lastReport[m] = snap.Utc;
                fired.Add(m);
            }
        }

        return fired;
    }
}