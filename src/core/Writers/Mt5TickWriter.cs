using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using backs2.Core.MT5;

namespace backs2.Core.Writers;

public static class Mt5TickWriter
{
    public static async Task<string> WriteAsync(
        string symbol,
        IReadOnlyList<Mt5Tick> ticks,
        string? outputDir = null)
    {
        outputDir ??= "data/ticks";
        Directory.CreateDirectory(outputDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = Path.Combine(outputDir, $"{symbol}_{stamp}.csv");

        var sb = new StringBuilder(ticks.Count * 64);
        sb.AppendLine("time_msc,bid,ask,last,volume,volume_real,flags");

        foreach (var t in ticks)
        {
            sb.Append(t.TimeMsc).Append(',');
            sb.Append(t.Bid.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(t.Ask.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(t.Last.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(t.Volume).Append(',');
            sb.Append(t.VolumeReal.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.AppendLine(t.Flags.ToString(CultureInfo.InvariantCulture));
        }

        await File.WriteAllTextAsync(path, sb.ToString());
        LogManager.Info(LogTag.Provider, $"MT5: saved {ticks.Count} ticks to {path}");
        return path;
    }

    public static async Task<List<Mt5Tick>> ReadAsync(string path)
    {
        var result = new List<Mt5Tick>();
        if (!File.Exists(path)) return result;

        var lines = await File.ReadAllLinesAsync(path);
        if (lines.Length == 0) return result;

        for (int i = 1; i < lines.Length; i++)
        {
            var parts = lines[i].Split(',');
            if (parts.Length < 7) continue;

            result.Add(new Mt5Tick(
                long.Parse(parts[0], CultureInfo.InvariantCulture),
                double.Parse(parts[1], CultureInfo.InvariantCulture),
                double.Parse(parts[2], CultureInfo.InvariantCulture),
                double.Parse(parts[3], CultureInfo.InvariantCulture),
                ulong.Parse(parts[4], CultureInfo.InvariantCulture),
                double.Parse(parts[5], CultureInfo.InvariantCulture),
                uint.Parse(parts[6], CultureInfo.InvariantCulture)));
        }

        return result;
    }
}

/// <summary>
/// Streaming CSV writer for MT5 ticks. Open once per fetch, Append per batch,
/// Complete when the fetch finishes. One async write per batch (not per tick),
/// so the socket read loop is never blocked by disk I/O.
/// </summary>
public sealed class Mt5TickStreamWriter : IAsyncDisposable
{
    private readonly StreamWriter _writer;

    public string Path { get; }
    public long Appended { get; private set; }

    private Mt5TickStreamWriter(string path, StreamWriter writer)
    {
        Path = path;
        _writer = writer;
    }

    public static async Task<Mt5TickStreamWriter> OpenAsync(
        string symbol, string? outputDir = null)
    {
        outputDir ??= "data/ticks";
        Directory.CreateDirectory(outputDir);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var path = System.IO.Path.Combine(outputDir, $"{symbol}_{stamp}.csv");

        var fs = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var sw = new StreamWriter(fs, new UTF8Encoding(false), 64 * 1024)
        {
            AutoFlush = false
        };

        await sw.WriteLineAsync("time_msc,bid,ask,last,volume,volume_real,flags");

        LogManager.Info(LogTag.Provider, $"MT5: stream writer opened {path}");
        return new Mt5TickStreamWriter(path, sw);
    }

    public async Task AppendAsync(IReadOnlyList<Mt5Tick> ticks)
    {
        if (ticks.Count == 0) return;

        // Build the whole batch as one string, then one async write.
        var sb = new StringBuilder(ticks.Count * 64);
        for (int i = 0; i < ticks.Count; i++)
        {
            var t = ticks[i];
            sb.Append(t.TimeMsc).Append(',');
            sb.Append(t.Bid.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(t.Ask.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(t.Last.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(t.Volume).Append(',');
            sb.Append(t.VolumeReal.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(t.Flags.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        await _writer.WriteAsync(sb.ToString());
        Appended += ticks.Count;
    }

    public async Task CompleteAsync()
    {
        await _writer.FlushAsync();
        _writer.Dispose();
        LogManager.Info(LogTag.Provider,
            $"MT5: stream writer closed {Path} ({Appended} ticks)");
    }

    public async ValueTask DisposeAsync()
    {
        try { await _writer.DisposeAsync(); } catch { /* best-effort */ }
    }
}