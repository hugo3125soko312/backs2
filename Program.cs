using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using backs2.Core;
using backs2.Core.Compute;
using backs2.Core.DataAutomation;

namespace backs2;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Worker mode: run OCR in isolation and exit before Avalonia loads.
        if (args.Length > 0 && args[0] == "--ocr-worker")
        {
            Environment.ExitCode = OcrWorkerHost.RunAsync().GetAwaiter().GetResult();
            return;
        }

        // Worker mode: compute (strategy / Monte Carlo) in isolation.
        // Spawned by ComputeWorkerManager; never loads Avalonia, never
        // initializes LogManager. Writes nothing to logs/.
        if (args.Length > 0 && args[0] == "--worker")
        {
            Environment.ExitCode = WorkerHost.RunAsync(args).GetAwaiter().GetResult();
            return;
        }

        // Phase 1 smoke test: exercise the worker end-to-end from a console
        // run and print frames. Used to verify the IPC plumbing before any
        // real compute migrates into the worker.
        if (args.Length > 0 && args[0] == "--compute-selftest")
        {
            RunComputeSelfTest(args).GetAwaiter().GetResult();
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
}