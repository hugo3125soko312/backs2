using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using backs2.Core;
using backs2.Core.Compute;
using backs2.Core.DataAutomation;
using backs2.Core.Metrics;
using backs2.Core.Models;
using backs2.Core.Runner;

namespace backs2;

public static class Program
{
    // WinExe projects do not inherit the parent console on Windows.
    // Attaching to the launching terminal makes Console.WriteLine visible
    // for CLI-style flags. No-op when the process has no parent console.
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    private const int ATTACH_PARENT_PROCESS = -1;

    [STAThread]
    public static void Main(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }

        if (args.Length > 0 && args[0] == "--ocr-worker")
        {
            Environment.ExitCode = OcrWorkerHost.RunAsync().GetAwaiter().GetResult();
            return;
        }

        if (args.Length > 0 && args[0] == "--worker")
        {
            Environment.ExitCode = WorkerHost.RunAsync(args).GetAwaiter().GetResult();
            return;
        }

        if (args.Length > 0 && args[0] == "--compute-selftest")
        {
            RunComputeSelfTest(args).GetAwaiter().GetResult();
            return;
        }

        if (args.Length > 0 && args[0] == "--verify-strategy-parallelism")
        {
            Environment.ExitCode = VerifyStrategyParallelismAsync().GetAwaiter().GetResult();
            return;
        }

        try
        {
            LogManager.Initialize();
            LogManager.Info(LogTag.App, "Process start");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.App, "Fatal exception", ex);
            LogManager.Shutdown();
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static async Task RunComputeSelfTest(string[] args)
    {
        int memMb = 256;
        bool cancel = false;

        foreach (var a in args)
        {
            if (a.StartsWith("--mem=", StringComparison.Ordinal))
                int.TryParse(a.Substring(6), out memMb);
            else if (a == "--cancel")
                cancel = true;
        }

        var manager = new ComputeWorkerManager();

        var config = new JobConfig(
            ScriptPath: "",
            CachePath: "",
            Params: new Dictionary<string, string>(),
            MemoryLimitMb: memMb);

        Console.WriteLine($"[selftest] memcap={memMb} MB  cancel={cancel}");

        using var cts = new CancellationTokenSource();
        int frameCount = 0;

        var progress = new Progress<JobProgress>(p =>
        {
            frameCount++;
            Console.WriteLine($"[selftest] frame #{frameCount}: {p.Phase} {p.SlicesElapsed}/{p.TotalSlices} ({p.PercentComplete:F1}%)");

            if (cancel && frameCount == 2)
            {
                Console.WriteLine("[selftest] cancel requested after frame #2");
                cts.Cancel();
            }
        });

        try
        {
            var result = await manager.RunJobAsync(config, progress, cts.Token);
            Console.WriteLine($"[selftest] result: trades={result.TradeCount} summary='{result.Summary}'");
            Console.WriteLine($"[selftest] PASS — {frameCount} progress frame(s)");
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"[selftest] CANCELLED — {frameCount} progress frame(s) received before cancel");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[selftest] FAIL — {ex.GetType().Name}: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    // ------------------------------------------------------------------
    // Phase 26B.1 — StrategyManager parallelism verification
    // ------------------------------------------------------------------

    /// <summary>
    /// Picks the first strategy in data/strategies/ that actually produces
    /// trades on synthetic data, then runs it N times serially (semaphore
    /// in place, baseline) followed by N times concurrently with the
    /// semaphore bypassed. If every parallel run produces a byte-identical
    /// trade list to the baseline, the compiled ScriptRunner is confirmed
    /// safe for concurrent invocation. Exit code 0 on PASS, 1 on FAIL.
    ///
    /// A strategy that emits zero trades produces a trivially-matching
    /// signature and proves nothing — the candidate loop rejects those.
    /// </summary>
    private static async Task<int> VerifyStrategyParallelismAsync()
    {
        const int RunsPerMode = 4;
        const int BarCount = 5000;
        const int MinTradesForSignal = 3;

        Console.WriteLine("[verify] starting");

        const string strategiesDir = "data/strategies";
        if (!Directory.Exists(strategiesDir))
        {
            Console.WriteLine("[verify] data/strategies directory not found");
            return 1;
        }

        var candidates = Directory.GetFiles(strategiesDir, "*.cs");
        if (candidates.Length == 0)
        {
            Console.WriteLine("[verify] no strategies in data/strategies/");
            return 1;
        }

        var data = GenerateSyntheticKlines(BarCount);
        Console.WriteLine($"[verify] synthetic data: {BarCount} bars");

        var registry = new MetricRegistry();

        // --- Pick a strategy that actually trades on this data ---
        string? chosenPath = null;
        string chosenSource = "";
        Dictionary<string, string> chosenDefaults = new();
        int chosenTradeCount = 0;

        foreach (var candidate in candidates)
        {
            string source = await File.ReadAllTextAsync(candidate);
            var declared = StrategyParameterParser.Parse(source);
            var defaults = new Dictionary<string, string>();
            foreach (var p in declared)
                defaults[p.Name] = p.DefaultValue;

            var probeStrat = new StrategyManager(registry) { SerializeBacktests = true };
            var probe = await probeStrat.RunBacktestFromFileAsync(candidate, data, defaults);

            int trades = probe.Success ? probe.Trades.Count : 0;
            Console.WriteLine($"[verify] candidate {Path.GetFileName(candidate)}: {(probe.Success ? trades + " trade(s)" : "FAILED — " + probe.Error)}");

            if (probe.Success && trades >= MinTradesForSignal)
            {
                chosenPath = candidate;
                chosenSource = source;
                chosenDefaults = defaults;
                chosenTradeCount = trades;
                break;
            }
        }

        if (chosenPath == null)
        {
            Console.WriteLine($"[verify] FAIL — no strategy in data/strategies/ produced >= {MinTradesForSignal} trade(s) on synthetic data.");
            Console.WriteLine("[verify] A 0-trade result matches trivially and does not test concurrency safety.");
            Console.WriteLine("[verify] Add a strategy that trades on a random walk, or feed real cached bars instead of synthetic.");
            return 1;
        }

        Console.WriteLine($"[verify] chosen strategy: {Path.GetFileName(chosenPath)} ({chosenTradeCount} trade(s), {chosenDefaults.Count} param(s) defaulted)");

        // --- Serial baseline ---
        var strat = new StrategyManager(registry) { SerializeBacktests = true };

        var baseline = await strat.RunBacktestFromFileAsync(chosenPath, data, chosenDefaults);
        if (!baseline.Success)
        {
            Console.WriteLine($"[verify] baseline failed: {baseline.Error}");
            return 1;
        }

        long baselineSig = Signature(baseline.Trades);
        Console.WriteLine($"[verify] baseline: {baseline.Trades.Count} trade(s), sig={baselineSig}");

        int serialMatches = 0;
        for (int i = 0; i < RunsPerMode; i++)
        {
            var r = await strat.RunBacktestFromFileAsync(chosenPath, data, chosenDefaults);
            if (r.Success && Signature(r.Trades) == baselineSig) serialMatches++;
        }
        Console.WriteLine($"[verify] serial: {serialMatches}/{RunsPerMode} match baseline");

        // --- Parallel: bypass the throttle entirely ---
        strat.SerializeBacktests = false;

        var tasks = new Task<StrategyRunResult>[RunsPerMode];
        for (int i = 0; i < RunsPerMode; i++)
            tasks[i] = strat.RunBacktestFromFileAsync(chosenPath, data, chosenDefaults);

        StrategyRunResult[] results;
        try
        {
            results = await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[verify] parallel threw: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        int parallelMatches = 0;
        for (int i = 0; i < RunsPerMode; i++)
        {
            var r = results[i];
            if (!r.Success)
            {
                Console.WriteLine($"[verify] parallel #{i}: failed — {r.Error}");
                continue;
            }

            long sig = Signature(r.Trades);
            if (sig == baselineSig)
            {
                parallelMatches++;
            }
            else
            {
                Console.WriteLine($"[verify] parallel #{i}: sig mismatch — expected {baselineSig}, got {sig} ({r.Trades.Count} trades)");
            }
        }
        Console.WriteLine($"[verify] parallel: {parallelMatches}/{RunsPerMode} match baseline");

        bool pass = serialMatches == RunsPerMode && parallelMatches == RunsPerMode;

        Console.WriteLine(pass
            ? "[verify] PASS — ScriptRunner is thread-safe; StrategyManager throttle can be bypassed by the optimizer"
            : "[verify] FAIL — ScriptRunner is not safe for concurrent invocation; keep the throttle");

        return pass ? 0 : 1;
    }

    private static List<Kline> GenerateSyntheticKlines(int count)
    {
        var rng = new Random(42);
        var list = new List<Kline>(count);
        double price = 100.0;
        var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        for (int i = 0; i < count; i++)
        {
            double open = price;
            double change = (rng.NextDouble() - 0.5) * 2.0;
            double close = open + change;
            double high = Math.Max(open, close) + rng.NextDouble() * 0.5;
            double low = Math.Min(open, close) - rng.NextDouble() * 0.5;
            long vol = 100 + rng.Next(900);

            list.Add(new Kline(t0.AddDays(i), open, high, low, close, vol));
            price = close;
        }

        return list;
    }

    private static long Signature(IReadOnlyList<Trade> trades)
    {
        unchecked
        {
            long h = 17;
            foreach (var t in trades)
            {
                h = h * 31 + (t.Side?.GetHashCode() ?? 0);
                h = h * 31 + t.EntryIndex;
                h = h * 31 + t.ExitIndex;
                h = h * 31 + BitConverter.DoubleToInt64Bits(t.EntryPrice);
                h = h * 31 + BitConverter.DoubleToInt64Bits(t.ExitPrice);
            }
            return h;
        }
    }
}