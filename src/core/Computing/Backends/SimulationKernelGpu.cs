using System;
using System.Runtime.InteropServices;
using backs2.Core.Computing;
using backs2.Core.Overview;
using ComputeSharp;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// The GPU-side view of a <see cref="TimeSlice"/>.
///
/// <para><b>Why a mirror exists.</b> <see cref="TimeSlice"/> is a persistence
/// format and is not changed for the GPU's benefit, and it cannot be a shader
/// buffer element as-is: a shader cannot carry <c>long</c> or <c>byte</c> fields.
/// The kernel only reads <c>MtmHigh</c>, <c>MtmLow</c>, <c>RealizedPnl</c> and the
/// EOD flag, so the mirror drops the timestamp entirely rather than widening it,
/// and widens the flag to <c>uint</c>.</para>
///
/// <para><b>Layout.</b> Fields are ordered widest-first so the struct needs no
/// padding: two doubles (16 bytes) then two uints (8 bytes), 24 bytes total in
/// both the C# marshalling rules and HLSL structured-buffer packing. The GPU
/// backend builds this array once per dispatch from the caller's
/// <see cref="TimeSlice"/> array.</para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct GpuTimeSlice
{
    public double MtmHigh;
    public double MtmLow;
    public double RealizedPnl;
    public uint IsEodReset;

    public static GpuTimeSlice[] From(TimeSlice[] slices)
    {
        var mirrored = new GpuTimeSlice[slices.Length];
        for (int i = 0; i < slices.Length; i++)
        {
            var s = slices[i];
            mirrored[i] = new GpuTimeSlice
            {
                MtmHigh = s.MtmHigh,
                MtmLow = s.MtmLow,
                RealizedPnl = s.RealizedPnl,
                IsEodReset = s.IsEodReset
            };
        }

        return mirrored;
    }
}

/// <summary>
/// The Monte Carlo state machine on the GPU (ROADMAP Phase 29B) — one GPU thread
/// per simulation, one ComputeSharp <c>[Shader]</c> dispatch per chunk.
///
/// <para><b>Semantics.</b> <see cref="MonteCarloSimulationShader.Execute"/>
/// reproduces the CPU kernel (<see cref="SimulationKernelCpu.Simulate"/>, itself
/// the verbatim port of the private <c>MonteCarloEngine.SimulateOnce</c> that
/// shipped through Phase 29A) statement for statement, in the same order:
/// MTM-low vs daily floor, then max-DD floor, then realize PnL, then HWM update,
/// then streak/drawdown bookkeeping, then the EOD reset. Two divergences from
/// the CPU path are deliberate and bounded:</para>
/// <list type="bullet">
///   <item><description><b>RNG.</b> A shader cannot instantiate
///   <c>System.Random</c>, so each thread seeds a Philox-4x32 counter from
///   (<c>seed</c>, absolute iteration). The stream is statistically equivalent,
///   not bit-identical — the acceptance criterion for this path is equivalent
///   aggregates, not equal doubles. Within the GPU backend, one seed always
///   produces one result.</description></item>
///   <item><description><b>Floating point.</b> The driver may contract
///   <c>a + b*c</c> into an FMA, so a per-slice balance can differ from the
///   CPU's in the last ulp. That is noise against a double balance, not
///   drift.</description></item>
/// </list>
///
/// <para><b>Determinism.</b> Every thread writes only its own slot of every
/// output buffer and derives its RNG state purely from its absolute iteration
/// index, so there is no cross-thread reduction, no shared mutable state, and no
/// dependence on wavefront scheduling. A given seed produces the same bytes on
/// every run.</para>
///
/// <para><b>Shader-type constraints.</b> ComputeSharp 3.2.0 exposes no HLSL
/// vector types to a shader and rejects <c>long</c> and <c>byte</c> fields, so
/// the Philox state is carried as plain <c>uint</c> scalars and the 64-bit
/// products Philox needs are split by hand into 16-bit partial products.</para>
/// </summary>
public sealed class SimulationKernelGpu
{
    /// <summary>
    /// Threads per GPU thread group. 64 is the default X group size and a multiple
    /// of every vendor's wavefront, so no partially populated wave is left behind.
    /// </summary>
    public const int ThreadsPerGroup = 64;

    private readonly GraphicsDevice _device;

    public SimulationKernelGpu(GraphicsDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <summary>
    /// Gets the device the kernel dispatches to.
    /// </summary>
    public GraphicsDevice Device => _device;

    /// <summary>
    /// Runs <paramref name="iterationCount"/> simulations, thread <c>t</c>
    /// simulating absolute iteration <c>startIteration + t</c>, and returns their
    /// per-iteration results indexed from zero.
    /// </summary>
    /// <remarks>
    /// Fail soft: any device, driver, or shader failure falls back to
    /// <paramref name="fallback"/> for this chunk rather than propagating. The
    /// caller's iteration range is unchanged, so a fallback is invisible in the
    /// result shape and only visible in the log.
    /// </remarks>
    public MonteCarloDispatchResult RunChunk(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        int startIteration,
        int iterationCount,
        int? seed,
        bool retainCurves,
        CpuBackend fallback)
    {
        if (slices == null || slices.Length == 0 || request.TargetDurationSlices <= 0 || iterationCount <= 0)
            return MonteCarloDispatchResult.Empty;

        if (retainCurves && CurveBufferWouldExceedDevice(iterationCount, request.TargetDurationSlices))
        {
            LogManager.Warn(LogTag.MonteCarlo,
                $"compute: GPU curve buffer for {iterationCount} iteration(s) x " +
                $"{request.TargetDurationSlices} slice(s) is larger than half of '{_device.Name}'s " +
                "dedicated memory — running this chunk on the CPU backend");

            return fallback.RunIterationsAsync(
                slices, request, startIteration, iterationCount, seed, retainCurves, default)
                .GetAwaiter().GetResult();
        }

        try
        {
            var uniforms = new SimulationKernelGpuUniforms
            {
                TargetDurationSlices = request.TargetDurationSlices,
                StartingCapital = request.StartingCapital,
                DailyFloorLoss = request.DailyFloorLoss,
                MaxFloorLoss = request.MaxFloorLoss,
                ProfitTarget = request.ProfitTarget,
                ExpectedBlockSize = request.ExpectedBlockSize,
                TrailingEod = request.TrailingEod ? 1 : 0,
                TrailingIntraday = request.TrailingIntraday ? 1 : 0,
                Seed = seed ?? 0,
                StartIteration = startIteration,
                RetainCurve = retainCurves ? 1 : 0
            };

            var mirroredSlices = GpuTimeSlice.From(slices);

            using var sliceBuffer = _device.AllocateReadOnlyBuffer(mirroredSlices);

            using var terminalBuffer = _device.AllocateReadWriteBuffer<double>(iterationCount);
            using var outcomeBuffer = _device.AllocateReadWriteBuffer<int>(iterationCount);
            using var elapsedBuffer = _device.AllocateReadWriteBuffer<int>(iterationCount);
            using var drawdownBuffer = _device.AllocateReadWriteBuffer<double>(iterationCount);
            using var ratioBuffer = _device.AllocateReadWriteBuffer<double>(iterationCount);
            using var losingBuffer = _device.AllocateReadWriteBuffer<int>(iterationCount);
            using var flatBuffer = _device.AllocateReadWriteBuffer<int>(iterationCount);
            using var causeBuffer = _device.AllocateReadWriteBuffer<int>(iterationCount);
            using var curveBuffer = _device.AllocateReadWriteBuffer<double>(
                retainCurves ? iterationCount * request.TargetDurationSlices : 1);

            _device.For(
                iterationCount,
                new MonteCarloSimulationShader(
                    sliceBuffer,
                    curveBuffer,
                    terminalBuffer,
                    outcomeBuffer,
                    elapsedBuffer,
                    drawdownBuffer,
                    ratioBuffer,
                    losingBuffer,
                    flatBuffer,
                    causeBuffer,
                    uniforms));

            return new MonteCarloDispatchResult(
                CopyBack(terminalBuffer, iterationCount),
                CopyBackAsBytes(outcomeBuffer, iterationCount),
                CopyBack(elapsedBuffer, iterationCount),
                CopyBack(drawdownBuffer, iterationCount),
                CopyBack(ratioBuffer, iterationCount),
                CopyBack(losingBuffer, iterationCount),
                CopyBack(flatBuffer, iterationCount),
                CopyBackAsBytes(causeBuffer, iterationCount),
                CopyCurves(curveBuffer, iterationCount, request.TargetDurationSlices, retainCurves));
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.MonteCarlo,
                $"compute: GPU chunk [{startIteration}, {startIteration + iterationCount}) failed on " +
                $"'{_device.Name}' — running it on the CPU backend instead. " +
                $"{ex.GetType().Name}: {ex.Message}", ex);

            return fallback.RunIterationsAsync(
                slices, request, startIteration, iterationCount, seed, retainCurves, default)
                .GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// True when the retained-curve output for one chunk would need more than half
    /// the device's dedicated memory. Checked per chunk so a large iteration count
    /// is rejected before the allocation, not by it.
    /// </summary>
    private bool CurveBufferWouldExceedDevice(int iterationCount, int targetDurationSlices)
    {
        ulong bytes = (ulong)iterationCount * (ulong)targetDurationSlices * sizeof(double);
        return bytes > (ulong)_device.DedicatedMemorySize / 2UL;
    }

    /// <summary>
    /// Device-to-host copy of a single output buffer. Every value the kernel wrote
    /// is read back exactly once, into the array the engine will own.
    /// </summary>
    private static T[] CopyBack<T>(ReadWriteBuffer<T> buffer, int count)
        where T : unmanaged
    {
        var values = new T[count];
        buffer.CopyTo(values);
        return values;
    }

    /// <summary>
    /// Device-to-host copy with a narrowing to <see cref="byte"/>. The kernel
    /// carries the outcome and ruin-cause enums as <c>int</c> because a shader
    /// cannot hold a <c>byte</c>; the engine's result shape wants bytes.
    /// </summary>
    private static byte[] CopyBackAsBytes(ReadWriteBuffer<int> buffer, int count)
    {
        var raw = new int[count];
        buffer.CopyTo(raw);

        var values = new byte[count];
        for (int i = 0; i < count; i++)
            values[i] = (byte)raw[i];

        return values;
    }

    /// <summary>
    /// Slices the flat per-thread curve buffer back into one array per iteration.
    /// Iteration <c>t</c> owns the contiguous block
    /// <c>[t × targetDurationSlices, (t + 1) × targetDurationSlices)</c>, so the
    /// split is a set of copies with no reinterpretation.
    /// </summary>
    private static double[][] CopyCurves(
        ReadWriteBuffer<double> buffer,
        int iterationCount,
        int targetDurationSlices,
        bool retainCurves)
    {
        if (!retainCurves)
            return Array.Empty<double[]>();

        var flat = new double[iterationCount * targetDurationSlices];
        buffer.CopyTo(flat);

        var curves = new double[iterationCount][];
        for (int t = 0; t < iterationCount; t++)
        {
            var curve = new double[targetDurationSlices];
            Array.Copy(flat, t * targetDurationSlices, curve, 0, targetDurationSlices);
            curves[t] = curve;
        }

        return curves;
    }
}

/// <summary>
/// The shader-side mirror of <see cref="MonteCarloSimulationRequest"/> plus the
/// dispatch keys. Exists instead of passing the request itself because shader
/// parameters are blitted into a constant buffer by the source generator, and a
/// record struct's compiler-generated members are not part of that contract.
/// Field order is the constant-buffer layout; <see cref="SimulationKernelGpu"/>
/// is the only place that translates between the two.
/// </summary>
public struct SimulationKernelGpuUniforms
{
    public int TargetDurationSlices;
    public int DailyFloorLoss;
    public int MaxFloorLoss;
    public int ProfitTarget;
    public int ExpectedBlockSize;
    public int TrailingEod;
    public int TrailingIntraday;
    public int Seed;
    public int StartIteration;
    public int RetainCurve;
    public double StartingCapital;
}

/// <summary>
/// The ComputeSharp kernel. One thread per simulation; <c>ThreadIds.X</c> is the
/// flat dispatch index, so thread <c>t</c> is absolute iteration
/// <c>StartIteration + t</c>.
/// </summary>
[ThreadGroupSize(SimulationKernelGpu.ThreadsPerGroup, 1, 1)]
[GeneratedComputeShaderDescriptor]
[RequiresDoublePrecisionSupport]
public readonly partial struct MonteCarloSimulationShader(
    ReadOnlyBuffer<GpuTimeSlice> slices,
    ReadWriteBuffer<double> curves,
    ReadWriteBuffer<double> terminalBalances,
    ReadWriteBuffer<int> outcomes,
    ReadWriteBuffer<int> slicesElapsed,
    ReadWriteBuffer<double> maxDrawdowns,
    ReadWriteBuffer<double> bestDayRatios,
    ReadWriteBuffer<int> longestLosingStreaks,
    ReadWriteBuffer<int> longestFlatStretches,
    ReadWriteBuffer<int> ruinCauses,
    SimulationKernelGpuUniforms u) : IComputeShader
{
    // MonteCarloOutcome / MonteCarloRuinCause codes, mirrored so the shader does
    // not reference managed constants. These are the persistence format — do not
    // renumber.
    private const int OutcomeBlown = 0;
    private const int OutcomeFunded = 1;
    private const int OutcomeTimeout = 2;

    private const int CauseNone = 0;
    private const int CauseDailyFloor = 1;
    private const int CauseMaxFloor = 2;

    /// <summary>Philox-4x32 multiplier (2^32 / golden ratio), split into its two words.</summary>
    private const uint PhiloxMulHigh = 0xD2511F53u;
    private const uint PhiloxMulLow = 0xCD9E8D57u;

    /// <summary>Weyl constants the key advances by every two rounds.</summary>
    private const uint WeylA = 0x9E3779B9u;
    private const uint WeylB = 0xBB67AE85u;

    public void Execute()
    {
        int t = ThreadIds.X;
        int n = slices.Length;
        int targetDurationSlices = u.TargetDurationSlices;

        // ---- Philox-4x32 state, seeded from (seed, absolute iteration) ----
        // Mixing the iteration into every counter word keeps neighbouring threads
        // in unrelated stream positions; a plain seed+iteration counter would put
        // them one draw apart on the first block.
        uint h = Mix((uint)(u.StartIteration + t) ^ (uint)u.Seed);

        uint counterX = h;
        uint counterY = Mix(h ^ 0x9E3779B9u);
        uint counterZ = Mix(h ^ 0x85EBCA6Bu);
        uint counterW = Mix(h ^ 0xC2B2AE35u);
        uint keyX = Mix((uint)u.Seed ^ 0x27D4EB2Fu);
        uint keyY = Mix((uint)u.Seed ^ 0x165667B1u);

        // The four words of the current block and the lane within it.
        uint wordX = 0;
        uint wordY = 0;
        uint wordZ = 0;
        uint wordW = 0;
        uint wordLane = 4;

        // ---- Simulation state (mirrors SimulationKernelCpu.Simulate) ----
        double startingCapital = u.StartingCapital;
        double balance = startingCapital;
        double hwm = startingCapital;
        double dayStartBalance = startingCapital;
        double peak = startingCapital;
        double maxDd = 0;

        double dayRealized = 0;
        double maxDayRealized = 0;

        int losingStreak = 0;
        int longestLosing = 0;

        int flatStretch = 0;
        int longestFlat = 0;

        int cause = CauseNone;
        int elapsed = 0;
        int result = OutcomeTimeout;

        int produced = 0;
        double p = 1.0 / u.ExpectedBlockSize;

        bool ended = false;

        while (produced < targetDurationSlices && !ended)
        {
            int start = NextIndex(
                (uint)n,
                ref counterX, ref counterY, ref counterZ, ref counterW,
                ref keyX, ref keyY,
                ref wordX, ref wordY, ref wordZ, ref wordW, ref wordLane);

            int blockLen = 1;
            while (NextDouble(
                       ref counterX, ref counterY, ref counterZ, ref counterW,
                       ref keyX, ref keyY,
                       ref wordX, ref wordY, ref wordZ, ref wordW, ref wordLane) > p
                   && blockLen < n)
            {
                blockLen++;
            }

            for (int k = 0; k < blockLen && produced < targetDurationSlices; k++, produced++)
            {
                var s = slices[(start + k) % n];

                if (s.IsEodReset != 0u)
                {
                    if (dayRealized > maxDayRealized) maxDayRealized = dayRealized;
                    dayRealized = 0;

                    dayStartBalance = balance;

                    if (u.TrailingEod != 0 && balance > hwm) hwm = balance;
                }

                double mtmBalance = balance + s.MtmLow;

                double dailyFloor = dayStartBalance - u.DailyFloorLoss;

                double maxFloor;
                if (u.TrailingIntraday != 0)
                    maxFloor = hwm - u.MaxFloorLoss;
                else if (u.TrailingEod != 0)
                    maxFloor = hwm - u.MaxFloorLoss;
                else
                    maxFloor = startingCapital - u.MaxFloorLoss;

                bool dailyHit = mtmBalance <= dailyFloor;
                bool maxHit = mtmBalance <= maxFloor;

                if (dailyHit || maxHit)
                {
                    double worst = dailyFloor < maxFloor ? dailyFloor : maxFloor;
                    balance = worst;
                    if (u.RetainCurve != 0) curves[t * targetDurationSlices + produced] = balance;
                    result = OutcomeBlown;
                    elapsed = produced + 1;

                    if (dailyHit && maxHit)
                        cause = dailyFloor >= maxFloor ? CauseDailyFloor : CauseMaxFloor;
                    else if (dailyHit)
                        cause = CauseDailyFloor;
                    else
                        cause = CauseMaxFloor;

                    ended = true;
                    break;
                }

                double peakBefore = peak;
                double mtmHigh = balance + s.MtmHigh;
                if (mtmHigh > peak) peak = mtmHigh;

                balance += s.RealizedPnl;
                dayRealized += s.RealizedPnl;

                if (s.RealizedPnl < 0)
                {
                    losingStreak++;
                    if (losingStreak > longestLosing) longestLosing = losingStreak;
                }
                else if (s.RealizedPnl > 0)
                {
                    losingStreak = 0;
                }

                if (peak > peakBefore)
                    flatStretch = 0;
                else
                    flatStretch++;
                if (flatStretch > longestFlat) longestFlat = flatStretch;

                if (u.TrailingIntraday != 0 && balance > hwm) hwm = balance;

                double dd = peak > 0 ? (peak - balance) / peak : 0;
                if (dd > maxDd) maxDd = dd;

                if (u.RetainCurve != 0) curves[t * targetDurationSlices + produced] = balance;

                if (u.ProfitTarget > 0 && balance - startingCapital >= u.ProfitTarget)
                {
                    result = OutcomeFunded;
                    elapsed = produced + 1;
                    ended = true;
                    break;
                }
            }
        }

        if (!ended)
            elapsed = targetDurationSlices;

        if (dayRealized > maxDayRealized) maxDayRealized = dayRealized;

        double terminalBalance = balance;
        double profit = terminalBalance - startingCapital;

        terminalBalances[t] = terminalBalance;
        outcomes[t] = result;
        slicesElapsed[t] = elapsed;
        maxDrawdowns[t] = maxDd * 100.0;
        bestDayRatios[t] = (profit > 0 && maxDayRealized > 0) ? maxDayRealized / profit : 0.0;
        longestLosingStreaks[t] = longestLosing;
        longestFlatStretches[t] = longestFlat;
        ruinCauses[t] = cause;
    }

    // ------------------------------------------------------------------
    // Philox-4x32-10
    //
    // A counter-based generator is the right shape here: a shader cannot carry
    // System.Random's state machine, every thread can seed itself from
    // (seed, iteration) with no shared state or stream jumping, and the output
    // depends only on the counter — so a thread's stream is identical regardless
    // of which wavefront it lands in.
    //
    // Bit-identical output with System.Random is not achievable and is not
    // attempted: the algorithms and their rounding differ. The acceptance
    // criterion for the GPU path is statistical equivalence of the aggregates.
    //
    // The state is carried as plain uint scalars, not an HLSL uvec4, because
    // ComputeSharp 3.2.0 does not expose vector types to shader code.
    // ------------------------------------------------------------------

    /// <summary>Draws the next 32-bit word, computing a fresh block every four draws.</summary>
    private static uint NextWord(
        ref uint counterX, ref uint counterY, ref uint counterZ, ref uint counterW,
        ref uint keyX, ref uint keyY,
        ref uint wordX, ref uint wordY, ref uint wordZ, ref uint wordW,
        ref uint wordLane)
    {
        if (wordLane >= 4u)
        {
            PhiloxBlock(
                counterX, counterY, counterZ, counterW, keyX, keyY,
                ref wordX, ref wordY, ref wordZ, ref wordW);

            // Counter increment, carrying across the 128-bit value.
            counterX += 1u;
            if (counterX == 0u)
            {
                counterY += 1u;
                if (counterY == 0u)
                {
                    counterZ += 1u;
                    if (counterZ == 0u) counterW += 1u;
                }
            }

            wordLane = 0u;
        }

        uint word;
        if (wordLane == 0u) word = wordX;
        else if (wordLane == 1u) word = wordY;
        else if (wordLane == 2u) word = wordZ;
        else word = wordW;

        wordLane += 1u;
        return word;
    }

    /// <summary>
    /// A double in [0, 1). 53 bits of mantissa from two consecutive words — the
    /// same width <c>System.Random.NextDouble</c> draws — so the two backends have
    /// comparable resolution.
    /// </summary>
    private static double NextDouble(
        ref uint counterX, ref uint counterY, ref uint counterZ, ref uint counterW,
        ref uint keyX, ref uint keyY,
        ref uint wordX, ref uint wordY, ref uint wordZ, ref uint wordW,
        ref uint wordLane)
    {
        uint a = NextWord(
            ref counterX, ref counterY, ref counterZ, ref counterW,
            ref keyX, ref keyY,
            ref wordX, ref wordY, ref wordZ, ref wordW, ref wordLane);

        uint b = NextWord(
            ref counterX, ref counterY, ref counterZ, ref counterW,
            ref keyX, ref keyY,
            ref wordX, ref wordY, ref wordZ, ref wordW, ref wordLane);

        return ((double)(a >> 5) * 67108864.0 + (double)(b >> 6)) / 9007199254740992.0;
    }

    /// <summary>
    /// The raw <c>rng.Next(n)</c> equivalent: a uniform index in <c>[0, n)</c>,
    /// produced by the same mask-and-reject strategy
    /// <c>System.Random.Next(int)</c> uses, so slice selection is unbiased for
    /// slice counts that are not powers of two.
    /// </summary>
    private static int NextIndex(
        uint max,
        ref uint counterX, ref uint counterY, ref uint counterZ, ref uint counterW,
        ref uint keyX, ref uint keyY,
        ref uint wordX, ref uint wordY, ref uint wordZ, ref uint wordW,
        ref uint wordLane)
    {
        if (max == 0u) return 0;

        uint bound = max - 1u;
        bound |= bound >> 1;
        bound |= bound >> 2;
        bound |= bound >> 4;
        bound |= bound >> 8;
        bound |= bound >> 16;

        uint value;
        do
        {
            value = NextWord(
                ref counterX, ref counterY, ref counterZ, ref counterW,
                ref keyX, ref keyY,
                ref wordX, ref wordY, ref wordZ, ref wordW, ref wordLane) & bound;
        }
        while (value > max - 1u);

        return (int)value;
    }

    /// <summary>
    /// One Philox-4x32-10 block: ten rounds of the round function, with the key
    /// advanced by the Weyl constants every two rounds, exactly as the Random123
    /// specification defines it.
    /// </summary>
    private static void PhiloxBlock(
        uint counterX, uint counterY, uint counterZ, uint counterW,
        uint keyX, uint keyY,
        ref uint outX, ref uint outY, ref uint outZ, ref uint outW)
    {
        uint stateX = counterX;
        uint stateY = counterY;
        uint stateZ = counterZ;
        uint stateW = counterW;

        uint roundKeyX = keyX;
        uint roundKeyY = keyY;

        for (int round = 0; round < 10; round++)
        {
            PhiloxRound(
                stateX, stateY, stateZ, stateW, roundKeyX, roundKeyY,
                ref stateX, ref stateY, ref stateZ, ref stateW);

            if ((round & 1) == 1)
            {
                roundKeyX += WeylA;
                roundKeyY += WeylB;
            }
        }

        outX = stateX;
        outY = stateY;
        outZ = stateZ;
        outW = stateW;
    }

    /// <summary>
    /// One Philox-4x32 round: two 32×32→64 multiplies with the high and low halves
    /// crossed over, xor-ed against the counter and the round key.
    /// </summary>
    private static void PhiloxRound(
        uint counterX, uint counterY, uint counterZ, uint counterW,
        uint keyX, uint keyY,
        ref uint outX, ref uint outY, ref uint outZ, ref uint outW)
    {
        uint loHigh = MulHigh(PhiloxMulHigh, counterX);
        uint loLow = PhiloxMulHigh * counterX;

        uint hiHigh = MulHigh(PhiloxMulLow, counterZ);
        uint hiLow = PhiloxMulLow * counterZ;

        outX = hiHigh ^ counterY ^ keyX;
        outY = hiLow;
        outZ = loHigh ^ counterW ^ keyY;
        outW = loLow;
    }

    /// <summary>
    /// High 32 bits of <paramref name="a"/> × <paramref name="b"/>, computed from
    /// 16-bit partial products only. Shader targets do not portably expose 64-bit
    /// integer multiplication, and Philox needs the high half.
    /// </summary>
    private static uint MulHigh(uint a, uint b)
    {
        uint aLow = a & 0xFFFFu;
        uint aHigh = a >> 16;
        uint bLow = b & 0xFFFFu;
        uint bHigh = b >> 16;

        uint p0 = aLow * bLow;
        uint p1 = aLow * bHigh;
        uint p2 = aHigh * bLow;
        uint p3 = aHigh * bHigh;

        uint carry = (p0 >> 16) + (p1 & 0xFFFFu) + (p2 & 0xFFFFu);
        return p3 + (p1 >> 16) + (p2 >> 16) + (carry >> 16);
    }

    /// <summary>
    /// One round of the Murmur3-style finalizer, used to decorrelate the seed
    /// words. Integer-only and deterministic, so it is identical on every device.
    /// </summary>
    private static uint Mix(uint x)
    {
        x ^= x >> 16;
        x *= 0x7FEB352Du;
        x ^= x >> 15;
        x *= 0x846CA68Bu;
        x ^= x >> 16;
        return x;
    }
}
