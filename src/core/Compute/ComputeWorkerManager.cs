using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;

namespace backs2.Core.Compute;

public sealed class ComputeWorkerManager
{
    private const string WorkerArg = "--worker";

    public async Task<WorkerResult> RunJobAsync(
        JobConfig config,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        var (hostExe, hostPrefix) = ResolveHost();

        LogManager.Debug(LogTag.App,
            $"Compute worker: spawning '{hostExe} {hostPrefix}{WorkerArg}' (mem cap {config.MemoryLimitMb} MB)");

        using var serverOut = new AnonymousPipeServerStream(
            PipeDirection.Out, HandleInheritability.Inheritable);
        using var serverIn = new AnonymousPipeServerStream(
            PipeDirection.In, HandleInheritability.Inheritable);

        // AnonymousPipeServerStream directions, from the parent's perspective:
        //   serverOut (PipeDirection.Out) → parent WRITES, worker READS.
        //   serverIn  (PipeDirection.In)  → parent READS,  worker WRITES.
        // The client handle passed to the worker must match its own direction:
        //   --in  → worker reads  → serverOut's client handle
        //   --out → worker writes → serverIn's client handle
        var psi = new ProcessStartInfo
        {
            FileName = hostExe,
            Arguments = $"{hostPrefix}{WorkerArg} " +
                        $"--in={serverOut.GetClientHandleAsString()} " +
                        $"--out={serverIn.GetClientHandleAsString()}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        Process? process = null;
        JobObjectHandle? job = null;
        int workerPid = 0;

        try
        {
            process = Process.Start(psi);
            if (process == null)
            {
                LogManager.Error(LogTag.App, "Compute worker: Process.Start returned null");
                throw new InvalidOperationException("Failed to start compute worker process");
            }

            workerPid = process.Id;

            // Capture worker stderr/stdout into the parent log. The worker
            // uses Console.Error for diagnostics because it does not initialize
            // LogManager. Debug level so it lands in the file only.
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    LogManager.Debug(LogTag.App, $"[worker {workerPid} stderr] {e.Data}");
            };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    LogManager.Debug(LogTag.App, $"[worker {workerPid} stdout] {e.Data}");
            };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            // Now that the child has inherited the pipe handles, drop the local
            // copies so the server stream closes cleanly when the child exits.
            serverOut.DisposeLocalCopyOfClientHandle();
            serverIn.DisposeLocalCopyOfClientHandle();

            job = new JobObjectHandle();
            if (!job.TryAttach(process.Handle, config.MemoryLimitMb, out var jobError))
            {
                LogManager.Warn(LogTag.App,
                    $"Compute worker pid {workerPid}: Job Object attach failed — {jobError}");
            }
            else
            {
                LogManager.Info(LogTag.App,
                    $"Compute worker started (pid {workerPid}, mem cap {config.MemoryLimitMb} MB)");
            }

            try
            {
                await WorkerIpc.WriteAsync(serverOut, new WorkerEnvelope
                {
                    Kind = WorkerMessageKind.Start,
                    Config = config
                }, ct).ConfigureAwait(false);

                LogManager.Debug(LogTag.App,
                    $"Compute worker pid {workerPid}: start frame sent");
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.App,
                    $"Compute worker pid {workerPid}: failed to send start frame", ex);
                throw;
            }

            try
            {
                var result = await PumpLoopAsync(serverIn, progress, ct).ConfigureAwait(false);

                LogManager.Info(LogTag.App,
                    $"Compute worker pid {workerPid}: job complete — " +
                    $"trades={result.TradeCount}, \"{result.Summary}\"");

                return result;
            }
            catch (OperationCanceledException)
            {
                LogManager.Info(LogTag.App, $"Compute worker pid {workerPid}: cancelled by caller");
                throw;
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.App, $"Compute worker pid {workerPid}: job failed", ex);
                throw;
            }
        }
        catch (Exception ex) when (process == null || workerPid == 0)
        {
            LogManager.Error(LogTag.App, "Compute worker: failed before process start", ex);
            throw;
        }
        finally
        {
            if (ct.IsCancellationRequested && process != null && !process.HasExited)
            {
                try
                {
                    using var cancelCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                    await WorkerIpc.WriteAsync(serverOut,
                        new WorkerEnvelope { Kind = WorkerMessageKind.Cancel },
                        cancelCts.Token).ConfigureAwait(false);
                }
                catch { /* worker already gone — nothing to do */ }
            }

            if (process != null)
                TryKill(process);

            job?.Dispose();
            process?.Dispose();
        }
    }

    private static async Task<WorkerResult> PumpLoopAsync(
        Stream serverIn,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var envelope = await WorkerIpc.ReadAsync(serverIn, ct).ConfigureAwait(false);
            if (envelope == null)
                throw new InvalidOperationException("Compute worker closed the pipe without a result");

            switch (envelope.Kind)
            {
                case WorkerMessageKind.Progress:
                    if (envelope.Progress != null)
                        progress?.Report(envelope.Progress);
                    break;

                case WorkerMessageKind.Result:
                    return envelope.Result ?? new WorkerResult(0, "empty");

                case WorkerMessageKind.Error:
                    throw new InvalidOperationException(envelope.ErrorMessage ?? "Worker error");

                case WorkerMessageKind.Pong:
                    // Accepted during the handshake window; ignored once a real
                    // job is running.
                    break;

                default:
                    break;
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch
        {
            // Already dead or never started — nothing to do.
        }
    }

    /// <summary>
    /// Resolves the executable path and (for dotnet-hosted runs) the managed
    /// entry-assembly prefix that must be passed as the first argument.
    /// </summary>
    private static (string fileName, string prefix) ResolveHost()
    {
        var procPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve host process path");

        bool isDotnetHost = string.Equals(
            Path.GetFileNameWithoutExtension(procPath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);

        if (!isDotnetHost)
            return (procPath, "");

        // `dotnet run` / `dotnet backs2.dll` — Environment.ProcessPath is
        // dotnet.exe, and the managed entry assembly path is argv[0].
        var managedEntry = Environment.GetCommandLineArgs()[0];
        if (string.IsNullOrEmpty(managedEntry))
            throw new InvalidOperationException("Managed entry assembly path unavailable");

        return (procPath, $"\"{managedEntry}\" ");
    }
}