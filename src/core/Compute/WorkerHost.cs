using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.Compute;

public static class WorkerHost
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? inHandle  = ParseArg(args, "--in");
        string? outHandle = ParseArg(args, "--out");

        Console.Error.WriteLine(
            $"worker: entry args=[{string.Join(' ', args)}] in={inHandle ?? "<null>"} out={outHandle ?? "<null>"}");

        if (inHandle == null || outHandle == null)
        {
            Console.Error.WriteLine("worker: missing --in or --out");
            return 2;
        }

        try
        {
            await using var inStream  = new AnonymousPipeClientStream(PipeDirection.In,  inHandle);
            await using var outStream = new AnonymousPipeClientStream(PipeDirection.Out, outHandle);

            Console.Error.WriteLine("worker: pipe streams constructed");

            using var cts = new CancellationTokenSource();

            await RunLoopAsync(inStream, outStream, cts.Token).ConfigureAwait(false);

            Console.Error.WriteLine("worker: loop exited cleanly");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"worker: FATAL {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task RunLoopAsync(Stream inStream, Stream outStream, CancellationToken ct)
    {
        while (true)
        {
            var envelope = await WorkerIpc.ReadAsync(inStream, ct).ConfigureAwait(false);
            if (envelope == null)
            {
                Console.Error.WriteLine("worker: input pipe closed by parent");
                return;
            }

            Console.Error.WriteLine($"worker: received kind='{envelope.Kind}'");

            switch (envelope.Kind)
            {
                case WorkerMessageKind.Ping:
                    await WorkerIpc.WriteAsync(outStream,
                        new WorkerEnvelope { Kind = WorkerMessageKind.Pong }, ct).ConfigureAwait(false);
                    break;

                case WorkerMessageKind.Cancel:
                    Console.Error.WriteLine("worker: cancel received");
                    return;

                case WorkerMessageKind.Start:
                    await RunPlaceholderJobAsync(outStream, ct).ConfigureAwait(false);
                    return;

                default:
                    await WorkerIpc.WriteAsync(outStream, new WorkerEnvelope
                    {
                        Kind = WorkerMessageKind.Error,
                        ErrorMessage = $"Unknown kind: {envelope.Kind}"
                    }, ct).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Phase 1 placeholder. Emits 5 progress frames over ~1s, then a result,
    /// then returns and the process exits. Phase 2/3 replace this with the
    /// real strategy-run / Monte-Carlo worker.
    /// </summary>
    private static async Task RunPlaceholderJobAsync(Stream outStream, CancellationToken ct)
    {
        const int totalSteps = 5;

        Console.Error.WriteLine($"worker: starting placeholder job ({totalSteps} steps)");

        for (int i = 1; i <= totalSteps; i++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(200, ct).ConfigureAwait(false);

            await WorkerIpc.WriteAsync(outStream, new WorkerEnvelope
            {
                Kind = WorkerMessageKind.Progress,
                Progress = new JobProgress("warmup", i, totalSteps, i * 100.0 / totalSteps)
            }, ct).ConfigureAwait(false);
        }

        await WorkerIpc.WriteAsync(outStream, new WorkerEnvelope
        {
            Kind = WorkerMessageKind.Result,
            Result = new WorkerResult(0, "placeholder job complete")
        }, ct).ConfigureAwait(false);

        Console.Error.WriteLine("worker: placeholder job finished, result sent");
    }

    private static string? ParseArg(string[] args, string key)
    {
        var prefix = key + "=";
        foreach (var a in args)
            if (a.StartsWith(prefix, StringComparison.Ordinal))
                return a.Substring(prefix.Length);
        return null;
    }
}