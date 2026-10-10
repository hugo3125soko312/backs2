using System;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.Overview;
using ComputeSharp;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// GPU backend: ComputeSharp over the DirectX 12 backend (ROADMAP Phase 28).
///
/// The constructor probes for a device. Failure is expected on machines without a
/// DirectX 12 adapter, on remote sessions, and inside the sandboxed worker — so it
/// logs a Warn and stores a <see cref="CpuBackend"/> fallback instead of throwing,
/// and <see cref="RunSimulationsAsync"/> delegates transparently. This is the
/// repo-wide "services fail soft at boundaries" rule.
///
/// GPU ownership: the device belongs in the compute worker process
/// (src/core/Compute/WorkerHost.cs), never in the parent UI process. That is why
/// <c>App.axaml.cs</c> registers <see cref="CpuBackend"/> for <see cref="IComputeBackend"/>
/// and this type is not registered directly — Phase 28B resolves it inside the worker,
/// where holding a device is correct.
///
/// The monte carlo kernel itself is Phase 29B. Until that <c>[Shader]</c> exists this
/// backend is a verified-device probe plus an honest CPU delegation; it never reports
/// acceleration it does not perform.
/// </summary>
public sealed class GpuBackend : IComputeBackend
{
    private readonly CpuBackend _fallback;
    private readonly GraphicsDevice? _device;

    /// <param name="fallback">
    /// CPU backend used whenever no device is available and, while Phase 29B is
    /// pending, for every dispatch. A default instance is created when omitted.
    /// </param>
    public GpuBackend(CpuBackend? fallback = null)
    {
        _fallback = fallback ?? new CpuBackend();
        _device = TryProbeDevice();
    }

    /// <inheritdoc/>
    public string Name => _device != null ? $"GPU ({_device.Name})" : _fallback.Name;

    /// <inheritdoc/>
    /// <remarks>
    /// False even when a device was found: no accelerated kernel exists until
    /// Phase 29B, so claiming acceleration here would make the worker's
    /// "GPU: &lt;adapter&gt;" status line a lie.
    /// </remarks>
    public bool IsAccelerated => false;

    /// <summary>
    /// Gets the probed DirectX 12 device, or null when the probe failed.
    /// </summary>
    public GraphicsDevice? Device => _device;

    /// <inheritdoc/>
    public Task<double[]> RunSimulationsAsync(
        TimeSlice[] slices,
        int targetDurationSlices,
        int iterations,
        int? seed,
        CancellationToken ct)
    {
        // No device — the fallback carries the whole call, with no re-probe and
        // no per-call Warn noise (the constructor already logged once).
        if (_device == null)
            return _fallback.RunSimulationsAsync(slices, targetDurationSlices, iterations, seed, ct);

        // Phase 29B: dispatch the ported [Shader] kernel here, chunked under the
        // Windows TDR limit (~10k iterations per dispatch), with seed splitting
        // identical to the CPU path so seeded runs stay reproducible. The parent
        // process never reaches this method — the worker does.
        LogManager.Debug(LogTag.MonteCarlo,
            $"compute: GpuBackend has device '{_device.Name}' but no compiled kernel yet " +
            "(Phase 29B) — delegating to CPU");

        return _fallback.RunSimulationsAsync(slices, targetDurationSlices, iterations, seed, ct);
    }

    /// <summary>
    /// Probes for a DirectX 12 device. Returns null on any failure — a missing
    /// device must degrade the feature, never the process.
    /// </summary>
    private static GraphicsDevice? TryProbeDevice()
    {
        try
        {
            var device = GraphicsDevice.GetDefault();

            LogManager.Info(LogTag.MonteCarlo,
                $"compute: GPU device available — '{device.Name}' " +
                $"(computeUnits={device.ComputeUnits}, wavefront={device.WavefrontSize}, " +
                $"dedicatedMemory={device.DedicatedMemorySize / (1024 * 1024)} MB, " +
                $"hardwareAccelerated={device.IsHardwareAccelerated})");

            return device;
        }
        catch (Exception ex)
        {
            LogManager.Warn(LogTag.MonteCarlo,
                $"compute: no DirectX 12 compute device — falling back to CPU. {ex.GetType().Name}: {ex.Message}");

            return null;
        }
    }
}
