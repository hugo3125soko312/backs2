using System;
using Vortice.DXGI;

namespace backs2.Core.Diagnostics;

/// <summary>
/// Dedicated VRAM usage for the high-performance DXGI adapter (Phase 36B).
///
/// The first three cuts iterated EnumAdapters1 and threw a SharpGen
/// PointerUSize conversion exception on the terminating probe. This version
/// asks DXGI for the high-performance adapter by preference instead — one
/// call, no loop, no null-terminator edge case — which is also the adapter
/// ComputeSharp uses on a laptop.
///
/// Everything fails soft: no adapter, no DXGI, or an adapter without
/// IDXGIAdapter3 all yield nulls.
/// </summary>
public sealed class VramProbe : IDisposable
{
    private IDXGIAdapter3? _adapter;
    private bool _probed;

    public (double? UsedMb, double? BudgetMb) Query()
    {
        if (!_probed) { _probed = true; TryInit(); }
        if (_adapter == null) return (null, null);

        try
        {
            var info = _adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
            return (info.CurrentUsage / 1048576.0, info.Budget / 1048576.0);
        }
        catch
        {
            return (null, null);
        }
    }

    private void TryInit()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory6>(false);

            IDXGIAdapter1? a1 = null;
            var hr = factory.EnumAdapterByGpuPreference(0, GpuPreference.HighPerformance, out a1);
            if (hr.Failure || a1 == null) return;

            try
            {
                _adapter = a1.QueryInterface<IDXGIAdapter3>();
            }
            finally
            {
                a1.Dispose();
            }
        }
        catch
        {
            _adapter = null;
        }
    }

    public void Dispose()
    {
        _adapter?.Dispose();
        _adapter = null;
    }
}