using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;

namespace backs2.Core.DataAutomation;

public sealed class OcrWorkerClient
{
    public async Task<ParseResult> ParseAsync(
        ParseRequest request,
        IProgress<(int Current, int Total)> progress,
        CancellationToken ct)
    {
        var (exePath, arguments) = BuildWorkerCommand();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.CurrentDirectory
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start OCR worker process.");

        // The moment the caller cancels, kill the worker. ReadLineAsync then
        // returns null and the loop below throws OperationCanceledException.
        using var killOnCancel = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch { }
        });

        try
        {
            _ = Task.Run(() => DrainStderr(process.StandardError));

            var jobJson = JsonSerializer.Serialize(new
            {
                type = "job",
                symbol = request.Symbol,
                timeframe = request.Timeframe,
                useGpu = request.UseGpu
            });

            await process.StandardInput.WriteLineAsync(jobJson);
            await process.StandardInput.FlushAsync();

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var line = await process.StandardOutput.ReadLineAsync();
                if (line == null)
                {
                    if (ct.IsCancellationRequested)
                        throw new OperationCanceledException(ct);

                    throw new InvalidOperationException(
                        "OCR worker exited without returning a result.");
                }

                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();

                switch (type)
                {
                    case "progress":
                        progress.Report((
                            root.GetProperty("current").GetInt32(),
                            root.GetProperty("total").GetInt32()));
                        break;

                    case "log":
                        RouteWorkerLog(root);
                        break;

                    case "done":
                        return new ParseResult(
                            root.GetProperty("total").GetInt32(),
                            root.GetProperty("parsed").GetInt32(),
                            root.GetProperty("failed").GetInt32(),
                            root.GetProperty("outPath").GetString() ?? "");

                    case "error":
                        throw new InvalidOperationException(
                            root.GetProperty("message").GetString());
                }
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
            catch { }
        }
    }

    private static (string fileName, string arguments) BuildWorkerCommand()
    {
        var exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve current process path.");

        var exeName = Path.GetFileNameWithoutExtension(exePath);
        if (string.Equals(exeName, "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var asm = Assembly.GetEntryAssembly()?.Location
                ?? throw new InvalidOperationException("Cannot resolve entry assembly.");
            return (exePath, $"\"{asm}\" --ocr-worker");
        }

        return (exePath, "--ocr-worker");
    }

    private static void RouteWorkerLog(JsonElement root)
    {
        var level = root.GetProperty("level").GetString() ?? "INF";
        var message = root.GetProperty("message").GetString() ?? "";

        switch (level)
        {
            case "DBG": LogManager.Debug(LogTag.Data, "[OCR] " + message); break;
            case "WRN": LogManager.Warn(LogTag.Data, "[OCR] " + message); break;
            case "ERR": LogManager.Error(LogTag.Data, "[OCR] " + message); break;
            default:    LogManager.Info(LogTag.Data, "[OCR] " + message); break;
        }
    }

    private static async Task DrainStderr(StreamReader reader)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    LogManager.Debug(LogTag.Data, "[OCR-stderr] " + line);
            }
        }
        catch { }
    }
}