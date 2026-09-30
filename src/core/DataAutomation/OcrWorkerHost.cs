using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;

namespace backs2.Core.DataAutomation;

public static class OcrWorkerHost
{
    private static readonly object _writeLock = new();

    public static async Task<int> RunAsync()
    {
        // Any stray Console.Out write from a library would corrupt the JSON
        // protocol on stdout. Redirect it to stderr; the parent drains it.
        Console.SetOut(Console.Error);

        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();

        using var reader = new StreamReader(stdin, Encoding.UTF8);
        using var writer = new StreamWriter(stdout, Encoding.UTF8) { AutoFlush = true };

        // Route every LogManager write from this process back to the parent,
        // so all worker diagnostics end up in the main runtime log.
        LogManager.Logged += (_, entry) =>
        {
            WriteLine(writer, new
            {
                type = "log",
                level = entry.Level,
                message = entry.Message
            });
        };

        try
        {
            var jobLine = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(jobLine))
            {
                WriteLine(writer, new { type = "error", message = "Empty job." });
                return 1;
            }

            using var doc = JsonDocument.Parse(jobLine);
            var root = doc.RootElement;

            if (root.GetProperty("type").GetString() != "job")
            {
                WriteLine(writer, new { type = "error", message = "Expected job." });
                return 1;
            }

            var symbol = root.GetProperty("symbol").GetString() ?? "";
            var timeframe = root.GetProperty("timeframe").GetString() ?? "";
            var useGpu = root.GetProperty("useGpu").GetBoolean();

            var service = new ShotParsingService();

            var progress = new Progress<(int Current, int Total)>(p =>
            {
                WriteLine(writer, new
                {
                    type = "progress",
                    current = p.Current,
                    total = p.Total
                });
            });

            var result = await service.ParseAsync(
                new ParseRequest(symbol, timeframe, useGpu),
                progress,
                CancellationToken.None);

            WriteLine(writer, new
            {
                type = "done",
                total = result.TotalShots,
                parsed = result.ParsedShots,
                failed = result.FailedShots,
                outPath = result.OutputPath
            });

            return 0;
        }
        catch (Exception ex)
        {
            WriteLine(writer, new { type = "error", message = ex.Message });
            return 1;
        }
    }

    private static void WriteLine(StreamWriter w, object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        lock (_writeLock)
        {
            w.WriteLine(json);
        }
    }
}