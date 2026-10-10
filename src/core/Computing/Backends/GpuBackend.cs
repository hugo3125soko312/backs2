using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.Computing;
using backs2.Core.Overview;
using ComputeSharp;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// GPU backend: ComputeSharp over the DirectX 12 backend (ROADMAP Phase 28,
/// kernel shipped in Phase 29B).
///
/// <para><b>Device probe.</b> The constructor probes for a DirectX 12 adapter.
/// Failure is expected on machines without one, on remote sessions, and inside
/// sandboxes — so it logs a Warn and keeps <see cref="IsAccelerated"/> false with
/// a <see cref="CpuBackend"/> fallback instead of throwing, and every entry point
/// delegates transparently. This is the repo-wide "services fail soft at
/// boundaries" rule, and it is why the app starts and behaves identically on a
/// machine with no GPU.</para>
///
/// <para><b>Ownership.</b> This backend holds a device in-process. That is
/// correct for the parent UI process now: Phase 27 assumed the device belonged to
/// the Compute Worker process, but for in-process Monte Carlo there is no IPC
/// boundary to protect and holding the device where the workload runs is
/// simpler. Migrating the scheduler and the device into the worker remains a
/// separate, deferred concern that this file does not preclude.</para>
///
/// <para><b>Fallback granularity.</b> A chunk that fails on the device is
/// re-run on the CPU backend for the same absolute iteration range, so one bad
/// chunk degrades throughput and never the result. The only exception is the
/// "no device at all" case, which is decided once at construction and costs
/// nothing per call.</para>
/// </summary>
public sealed class GpuBackend : IComputeBackend
{
    /// <summary>
    /// Iterations per kernel dispatch inside one <see cref="RunIterationsAsync"/>
    /// call. Matches the scheduler's <c>McChunkSize</c>: the scheduler already
    /// bounds a call at 10k iterations, so the whole call is normally one
    /// dispatch, and a larger direct call is split here for the same TDR and
    /// cancellation reasons.
    /// </summary>
    private const int GpuIterationBatch = 10_000;

    private readonly CpuBackend _fallback;
    private readonly GraphicsDevice? _device;
    private readonly SimulationKernelGpu? _kernel;

    /// <param name="fallback">
    /// CPU backend used whenever no device is available, when the device is not
    /// hardware accelerated, and for any chunk the device fails. A default
    /// instance is created when omitted.
    /// </param>
    public GpuBackend(CpuBackend? fallback = null)
    {
        _fallback = fallback ?? new CpuBackend();
        _device = TryProbeDevice();

        // The kernel is bound to the device, so it is created once here rather
        // than per dispatch. A failure to build it degrades to the fallback, the
        // same way a missing device does.
        _kernel = _device != null && _device.IsHardwareAccelerated
            ? TryCreateKernel(_device)
            : null;

        if (_device != null && !_device.IsHardwareAccelerated)
        {
            LogManager.Warn(LogTag.MonteCarlo,
                $"compute: DirectX 12 device '{_device.Name}' is a software adapter — " +
                "not claiming GPU acceleration, CPU fallback in use");
        }
    }

    /// <inheritdoc/>
    public string Name => _device != null ? $"GPU ({_device.Name})" : _fallback.Name;

    /// <inheritdoc/>
    /// <remarks>
    /// True only when a DirectX 12 device was found <em>and</em> it reports
    /// hardware acceleration <em>and</em> the Monte Carlo kernel was created
    /// against it. A software adapter or a kernel-creation failure reports false,
    /// so the claim always matches what dispatch actually does.
    /// </remarks>
    public bool IsAccelerated => _kernel != null;

    /// <summary>
    /// Gets the probed DirectX 12 device, or null when the probe failed.
    /// </summary>
    public GraphicsDevice? Device => _device;

    /// <inheritdoc/>
    public Task<MonteCarloDispatchResult> RunSimulationsAsync(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        int iterations,
        int? seed,
        bool retainCurves,
        int maxDegreeOfParallelism,
        CancellationToken ct)
    {
        if (slices == null || slices.Length == 0 || request.TargetDurationSlices <= 0 || iterations <= 0)
            return Task.FromResult(MonteCarloDispatchResult.Empty);

        // No accelerated kernel: the fallback carries the whole call, with no
        // re-probe and no per-call Warn noise (the constructor already logged).
        if (_kernel == null)
            return _fallback.RunSimulationsAsync(
                slices, request, iterations, seed, retainCurves, maxDegreeOfParallelism, ct);

        var kernel = _kernel;

        return Task.Run(() =>
        {
            var chunks = new List<MonteCarloDispatchResult>();

            for (int start = 0; start < iterations; start += GpuIterationBatch)
            {
                if (ct.IsCancellationRequested)
                    break;

                int count = Math.Min(GpuIterationBatch, iterations - start);

                chunks.Add(kernel.RunChunk(slices, request, start, count, seed, retainCurves, _fallback));
            }

            return MonteCarloDispatchResult.Concat(chunks);
        }, CancellationToken.None);
    }

    /// <inheritdoc cref="IComputeBackend.RunIterationsAsync"/>
    /// <remarks>
    /// One chunk, one device dispatch (unless the chunk exceeds
    /// <see cref="GpuIterationBatch"/>, in which case it is split here). The slice
    /// array is uploaded once per call and reused across every dispatch the call
    /// makes, so the upload is paid per chunk rather than per dispatch — the
    /// scheduler already amortizes it over 10k iterations.
    /// </remarks>
    public Task<MonteCarloDispatchResult> RunIterationsAsync(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        int startIteration,
        int iterationCount,
        int? seed,
        bool retainCurves,
        CancellationToken ct)
    {
        if (slices == null || slices.Length == 0 || request.TargetDurationSlices <= 0 || iterationCount <= 0)
            return Task.FromResult(MonteCarloDispatchResult.Empty);

        if (_kernel == null)
            return _fallback.RunIterationsAsync(
                slices, request, startIteration, iterationCount, seed, retainCurves, ct);

        // A single GPU dispatch is one unit of work on one device queue; the
        // scheduler's workers are already the outer parallelism, and it claims
        // chunks from a shared queue rather than sending several at once.
        var kernel = _kernel;

        return Task.Run(() =>
        {
            var chunks = new List<MonteCarloDispatchResult>();

            for (int offset = 0; offset < iterationCount; offset += GpuIterationBatch)
            {
                if (ct.IsCancellationRequested)
                    break;

                int count = Math.Min(GpuIterationBatch, iterationCount - offset);

                chunks.Add(kernel.RunChunk(
                    slices, request, startIteration + offset, count, seed, retainCurves, _fallback));
            }

            return MonteCarloDispatchResult.Concat(chunks);
        }, CancellationToken.None);
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
                $"hardwareAccelerated={device.IsHardwareAccelerated}, " +
                $"doublePrecision={device.IsDoublePrecisionSupportAvailable()})");

            return device;
        }
        catch (Exception ex)
        {
            LogManager.Warn(LogTag.MonteCarlo,
                $"compute: no DirectX 12 compute device — falling back to CPU. {ex.GetType().Name}: {ex.Message}");

            return null;
        }
    }

    /// <summary>
    /// Builds the Monte Carlo kernel against <paramref name="device"/>. The
    /// constructor only stores the wrapper; the shader compiles on first
    /// dispatch. Returns null on failure so construction stays non-throwing.
    /// </summary>
    private static SimulationKernelGpu? TryCreateKernel(GraphicsDevice device)
    {
        try
        {
            var kernel = new SimulationKernelGpu(device);

            LogManager.Info(LogTag.MonteCarlo,
                $"compute: Monte Carlo GPU kernel ready on '{device.Name}' " +
                $"({SimulationKernelGpu.ThreadsPerGroup} threads/group, GpuIterationBatch={GpuIterationBatch})");

            return kernel;
        }
        catch (Exception ex)
        {
            LogManager.Warn(LogTag.MonteCarlo,
                $"compute: Monte Carlo GPU kernel could not be created — falling back to CPU. " +
                $"{ex.GetType().Name}: {ex.Message}");

            return null;
        }
    }
}
