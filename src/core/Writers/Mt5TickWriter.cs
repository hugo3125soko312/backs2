using System;
using System.Buffers;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        // Rent a scratch buffer large enough for the whole file. Realistic
        // gold ticks are ~55 bytes/row; 128 is headroom against wider
        // doubles and larger volumes. Utf8Formatter throws on true overflow
        // — that path is unreachable at these column widths.
        int cap = Mt5TickCsv.Header.Length + ticks.Count * 128 + 128;
        var rented = ArrayPool<byte>.Shared.Rent(cap);
        try
        {
            // Span work stays inside a synchronous helper — C# 12 forbids
            // Span<T> locals in async methods.
            int pos = Mt5TickCsv.WriteHeader(rented, 0);
            pos = Mt5TickCsv.WriteTicks(rented, pos, ticks);

            await using (var fs = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.Read,
                bufferSize: 256 * 1024, useAsync: true))
            {
                await fs.WriteAsync(rented.AsMemory(0, pos));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

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
///
/// v0.4.x: formats directly into a rented UTF-8 byte buffer via
/// Utf8Formatter — no StringBuilder, no string.ToString() per row, no
/// per-batch string allocation. The FileStream is written to directly,
/// bypassing StreamWriter's UTF-16 internal buffer.
/// </summary>
public sealed class Mt5TickStreamWriter : IAsyncDisposable
{
    private readonly FileStream _stream;
    private bool _completed;

    public string Path { get; }
    public long Appended { get; private set; }

    private Mt5TickStreamWriter(string path, FileStream stream)
    {
        Path = path;
        _stream = stream;
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
            bufferSize: 256 * 1024,
            useAsync: true);

        await fs.WriteAsync(Mt5TickCsv.Header.AsMemory(0, Mt5TickCsv.Header.Length));

        LogManager.Info(LogTag.Provider, $"MT5: stream writer opened {path}");
        return new Mt5TickStreamWriter(path, fs);
    }

    public async Task AppendAsync(IReadOnlyList<Mt5Tick> ticks)
    {
        if (ticks.Count == 0) return;

        int cap = ticks.Count * 128 + 128;
        var rented = ArrayPool<byte>.Shared.Rent(cap);
        try
        {
            int pos = Mt5TickCsv.WriteTicks(rented, 0, ticks);
            await _stream.WriteAsync(rented.AsMemory(0, pos));
            Appended += ticks.Count;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async Task CompleteAsync()
    {
        if (_completed) return;
        _completed = true;
        await _stream.FlushAsync();
        _stream.Dispose();
        LogManager.Info(LogTag.Provider,
            $"MT5: stream writer closed {Path} ({Appended} ticks)");
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.DisposeAsync(); } catch { /* best-effort */ }
    }
}

/// <summary>
/// CSV serialization helpers shared by both writers. Fixed column order:
/// time_msc,bid,ask,last,volume,volume_real,flags.
///
/// All methods are synchronous and accept byte[] + offset. This keeps
/// Span&lt;byte&gt; locals confined to non-async frames — C# 12 forbids
/// ref-like locals in async methods.
/// </summary>
internal static class Mt5TickCsv
{
    public static readonly byte[] Header =
        "time_msc,bid,ask,last,volume,volume_real,flags\n"u8.ToArray();

    /// <summary>Writes the header at <paramref name="startPos"/>. Returns the new position.</summary>
    public static int WriteHeader(byte[] dest, int startPos)
    {
        Buffer.BlockCopy(Header, 0, dest, startPos, Header.Length);
        return startPos + Header.Length;
    }

    /// <summary>Writes each tick as a CSV row. Returns the new position.</summary>
    public static int WriteTicks(byte[] dest, int startPos, IReadOnlyList<Mt5Tick> ticks)
    {
        Span<byte> span = dest;
        int pos = startPos;

        for (int i = 0; i < ticks.Count; i++)
        {
            var t = ticks[i];
            WriteLong(span, ref pos, t.TimeMsc);
            span[pos++] = (byte)',';
            WriteDouble(span, ref pos, t.Bid);
            span[pos++] = (byte)',';
            WriteDouble(span, ref pos, t.Ask);
            span[pos++] = (byte)',';
            WriteDouble(span, ref pos, t.Last);
            span[pos++] = (byte)',';
            WriteULong(span, ref pos, t.Volume);
            span[pos++] = (byte)',';
            WriteDouble(span, ref pos, t.VolumeReal);
            span[pos++] = (byte)',';
            WriteUInt(span, ref pos, t.Flags);
            span[pos++] = (byte)'\n';
        }

        return pos;
    }

    private static void WriteLong(Span<byte> dest, ref int pos, long v)
    {
        if (!Utf8Formatter.TryFormat(v, dest.Slice(pos), out int written))
            throw new InvalidOperationException("MT5 CSV buffer overflow (long)");
        pos += written;
    }

    private static void WriteULong(Span<byte> dest, ref int pos, ulong v)
    {
        if (!Utf8Formatter.TryFormat(v, dest.Slice(pos), out int written))
            throw new InvalidOperationException("MT5 CSV buffer overflow (ulong)");
        pos += written;
    }

    private static void WriteUInt(Span<byte> dest, ref int pos, uint v)
    {
        if (!Utf8Formatter.TryFormat(v, dest.Slice(pos), out int written))
            throw new InvalidOperationException("MT5 CSV buffer overflow (uint)");
        pos += written;
    }

    private static void WriteDouble(Span<byte> dest, ref int pos, double v)
    {
        if (!Utf8Formatter.TryFormat(v, dest.Slice(pos), out int written))
            throw new InvalidOperationException("MT5 CSV buffer overflow (double)");
        pos += written;
    }
}