using System;
using System.Runtime.InteropServices;

namespace backs2.Core.Diagnostics;

/// <summary>
/// NVIDIA device utilization via NVML (ROADMAP Phase 36C).
///
/// nvml.dll ships with the NVIDIA driver at System32. It is absent on AMD,
/// Intel and headless hosts, so every entry point fails soft and the field
/// stays null forever after one probe. No per-sample retry, no log spam.
///
/// Initialisation happens once. A failed init is cached as unavailable rather
/// than re-attempted every second.
/// </summary>
public sealed class NvmlProbe : IDisposable
{
    private bool _probed;
    private bool _available;
    private IntPtr _device;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandle(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int NvmlDeviceGetUtilization(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    public double? Query()
    {
        if (!_probed) { _probed = true; TryInit(); }
        if (!_available) return null;

        try
        {
            if (NvmlDeviceGetUtilization(_device, out var u) != 0) return null;
            return u.Gpu;
        }
        catch
        {
            return null;
        }
    }

    private void TryInit()
    {
        try
        {
            if (NvmlInit() != 0) return;
            if (NvmlDeviceGetHandle(0, out _device) != 0) return;
            _available = true;
        }
        catch (DllNotFoundException)
        {
            _available = false;
        }
        catch
        {
            _available = false;
        }
    }

    public void Dispose()
    {
        if (_available)
        {
            try { NvmlShutdown(); } catch { }
        }
        _available = false;
    }
}