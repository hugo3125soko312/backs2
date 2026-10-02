// src/core/MT5/Tkc/TkcDecoder.cs
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace backs2.Core.MT5.Tkc;

public static class TkcDecoder
{
    private const uint TkcMagic = 0x000001FD;
    private const int HeaderMinSize = 0x0100;
    private const int SymbolScanStart = 0x00A0;
    private const int SymbolScanEndExclusive = 0x00D0;
    private const int SymbolReadBytes = 40;
    private const int RecordStride = 16;

    private const int IndexStart = 0x00E4;
    private const int IndexEntrySize = 48;
    private const int MaxIndexEntries = 4096;

    public static (TkcInfo Info, IReadOnlyList<TkcTick> Ticks) Decode(string path)
    {
        var sw = Stopwatch.StartNew();
        string fileName = Path.GetFileName(path);

        byte[] data;
        try { data = File.ReadAllBytes(path); }
        catch (Exception ex)
        {
            throw new TkcException($"Cannot read {path}: {ex.Message}", ex);
        }

        if (data.Length < HeaderMinSize)
            throw new TkcException($"File too small: {data.Length} bytes, need {HeaderMinSize}");

        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(0, 4));
        if (magic != TkcMagic)
            throw new TkcException($"Unsupported TKC version: magic=0x{magic:X8}");

        string symbol = ReadSymbol(data);
        var index = ParseIndex(data);
        var ticks = DecodeWithIndex(data, index);

        sw.Stop();
        long? startMs = ticks.Count > 0 ? ticks[0].TimestampMs : null;
        long? endMs   = ticks.Count > 0 ? ticks[ticks.Count - 1].TimestampMs : null;

        var info = new TkcInfo(magic, symbol, data.Length, ticks.Count, startMs, endMs);

        LogManager.Info(LogTag.Provider,
            $"TKC '{fileName}': {ticks.Count:N0} ticks, {index.Count} day(s), " +
            $"{sw.ElapsedMilliseconds:N0} ms");

        return (info, ticks);
    }

    // ── Index ───────────────────────────────────────────────────────

    private sealed record IndexEntry(
        ushort DayA, ushort DayB, uint Hash, uint Size, uint Offset, uint TickCount);

    private static List<IndexEntry> ParseIndex(byte[] data)
    {
        var entries = new List<IndexEntry>(256);
        uint prevOffset = 0;

        for (int i = 0; i < MaxIndexEntries; i++)
        {
            int pos = IndexStart + i * IndexEntrySize;
            if (pos + IndexEntrySize > data.Length) break;

            ushort dayA  = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            ushort dayB  = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos + 2, 2));
            uint   hash  = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 4, 4));
            uint   size  = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 8, 4));
            uint   offs  = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 12, 4));
            uint   ticks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos + 28, 4));

            if (size == 0 || offs == 0) break;
            if (offs <= prevOffset) break;
            if (offs >= data.Length) break;
            if (offs + size > data.Length) break;

            entries.Add(new IndexEntry(dayA, dayB, hash, size, offs, ticks));
            prevOffset = offs;
        }

        return entries;
    }

    // ── Header ──────────────────────────────────────────────────────

    private static string ReadSymbol(byte[] data)
    {
        for (int i = SymbolScanStart;
             i < SymbolScanEndExclusive && i + SymbolReadBytes <= data.Length;
             i += 2)
        {
            string s = Encoding.Unicode.GetString(data, i, SymbolReadBytes);
            int nullPos = s.IndexOf('\0');
            if (nullPos <= 0) continue;

            string candidate = s.Substring(0, nullPos);
            if (candidate.Length < 3) continue;
            if (!IsValidSymbol(candidate)) continue;

            return candidate;
        }

        throw new TkcException("Could not read symbol from TKC header");
    }

    private static bool IsValidSymbol(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsLetterOrDigit(c)) continue;
            if (c == '.' || c == '_' || c == '-' || c == '#') continue;
            return false;
        }
        return true;
    }

    // ── Per-day decode ──────────────────────────────────────────────

    private static List<TkcTick> DecodeWithIndex(byte[] data, List<IndexEntry> index)
    {
        var all = new List<TkcTick>(1_000_000);

        if (index.Count == 0)
        {
            var blocks = ExtractBlocks(data, 0, data.Length);
            var ticks  = DecodeFromBlocks(blocks);
            ticks.Sort(static (a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
            return ticks;
        }

        foreach (var entry in index)
        {
            int start = (int)entry.Offset;
            int end   = start + (int)entry.Size;
            if (end > data.Length) end = data.Length;
            if (end <= start) continue;

            var blocks   = ExtractBlocks(data, start, end);
            var dayTicks = DecodeFromBlocks(blocks);

            if (dayTicks.Count > 0) all.AddRange(dayTicks);
        }

        all.Sort(static (a, b) => a.TimestampMs.CompareTo(b.TimestampMs));
        return all;
    }

    // ── Block extraction ────────────────────────────────────────────

    private static List<TkcBlock> ExtractBlocks(byte[] data, int start, int end)
    {
        var blocks = new List<TkcBlock>(16);

        for (int i = start; i < end - 2; i++)
        {
            if (data[i] != 0x78) continue;

            byte flg = data[i + 1];
            if (flg != 0x01 && flg != 0x5E && flg != 0x9C && flg != 0xDA) continue;

            byte[] decompressed;
            try { decompressed = InflateZlib(data, i, end - i); }
            catch { continue; }

            if (decompressed.Length < 16) continue;

            var type = ClassifyBlock(decompressed);
            if (type == TkcBlockType.Unknown) continue;

            blocks.Add(new TkcBlock(decompressed, type, decompressed.Length, i));
        }

        return blocks;
    }

    private static byte[] InflateZlib(byte[] source, int offset, int length)
    {
        using var input = new MemoryStream(source, offset, length, writable: false);
        using var z = new ZLibStream(input, CompressionMode.Decompress);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int total = 0;
        try
        {
            while (true)
            {
                if (total == buffer.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, total);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = bigger;
                }

                int read = z.Read(buffer, total, buffer.Length - total);
                if (read == 0) break;
                total += read;
            }

            var result = new byte[total];
            Buffer.BlockCopy(buffer, 0, result, 0, total);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static TkcBlockType ClassifyBlock(byte[] data)
    {
        if (data.Length < 16) return TkcBlockType.Unknown;

        // Decisive: if the first 8 bytes are a valid Unix-ms timestamp, this
        // is a timestamp block. Deltas inside it can be mostly zero (many
        // ticks per millisecond), so a zero-fraction heuristic must not run
        // before this check.
        ulong first8 = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0, 8));
        if (first8 >= 1_420_000_000_000UL && first8 <= 2_000_000_000_000UL)
            return TkcBlockType.Timestamps;

        int n = data.Length / RecordStride;
        if (n == 0) return TkcBlockType.Unknown;

        int samples = Math.Min(64, Math.Max(1, n));
        int step    = Math.Max(1, n / samples);

        int priceHits = 0, total = 0;
        for (int i = 0; i < n; i += step)
        {
            int off = i * RecordStride;
            if (off + 16 > data.Length) break;
            total++;

            // The value may sit in the first 8 bytes (d0) or the second (d1)
            // depending on the block. Sample both halves.
            double a = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(off, 8));
            double b = BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(off + 8, 8));

            bool aIsPrice = a >= 0.1 && a <= 1_000_000.0;
            bool bIsPrice = b >= 0.1 && b <= 1_000_000.0;
            if (aIsPrice || bIsPrice) priceHits++;
        }

        if (total == 0) return TkcBlockType.Unknown;

        double priceFrac = (double)priceHits / total;
        return priceFrac > 0.3 ? TkcBlockType.Prices : TkcBlockType.Empty;
    }

    // ── Tick assembly ───────────────────────────────────────────────

    /// <summary>
    /// Walks blocks in file order and decodes one tick stream per
    /// [Timestamps, Prices..., (until next Timestamps)] group. A single day
    /// can contain many such groups when the terminal splits the day into
    /// multiple zlib payloads; earlier code kept only one group and dropped
    /// the rest.
    /// </summary>
    private static List<TkcTick> DecodeFromBlocks(List<TkcBlock> blocks)
    {
        var result = new List<TkcTick>();

        int i = 0;
        while (i < blocks.Count)
        {
            if (blocks[i].Type != TkcBlockType.Timestamps)
            {
                i++;
                continue;
            }

            var tsBlock = blocks[i];
            i++;

            var priceBlocks = new List<TkcBlock>(4);
            while (i < blocks.Count && blocks[i].Type != TkcBlockType.Timestamps)
            {
                if (blocks[i].Type == TkcBlockType.Prices)
                    priceBlocks.Add(blocks[i]);
                i++;
            }

            if (priceBlocks.Count < 2) continue;

            var (bidBlk, askBlk) = PickBidAsk(priceBlocks);
            if (bidBlk is null || askBlk is null) continue;

            var chunk = DecodeTickStream(tsBlock.Data, bidBlk.Value.Data, askBlk.Value.Data);
            if (chunk.Count > 0) result.AddRange(chunk);
        }

        return result;
    }

    private static (TkcBlock? bid, TkcBlock? ask) PickBidAsk(List<TkcBlock> priceBlocks)
    {
        if (priceBlocks.Count == 2)
        {
            var a = priceBlocks[0];
            var b = priceBlocks[1];
            return BlockRepValue(a.Data) < BlockRepValue(b.Data) ? (a, b) : (b, a);
        }

        TkcBlock? bestA = null, bestB = null;
        double bestDiff = double.MaxValue;

        for (int i = 0; i < priceBlocks.Count; i++)
        {
            double vi = BlockRepValue(priceBlocks[i].Data);
            for (int j = i + 1; j < priceBlocks.Count; j++)
            {
                double vj = BlockRepValue(priceBlocks[j].Data);
                double diff = Math.Abs(vi - vj);

                if (diff < bestDiff && diff < 20.0)
                {
                    bestDiff = diff;
                    bestA = priceBlocks[i];
                    bestB = priceBlocks[j];
                }
            }
        }

        if (bestA is null || bestB is null) return (null, null);

        double va = BlockRepValue(bestA.Value.Data);
        double vb = BlockRepValue(bestB.Value.Data);
        return va < vb ? (bestA, bestB) : (bestB, bestA);
    }

    private static double BlockRepValue(byte[] data)
    {
        int off = DetectPriceOffset(data);
        int mid = ((data.Length / 2) / RecordStride) * RecordStride;
        if (mid + off + 8 <= data.Length)
            return BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(mid + off, 8));
        return ReadDoubleAt(data, off);
    }

    /// <summary>
    /// Determines whether the f64 value in each record sits in the first
    /// (0) or second (8) half of the 16-byte record. Bid/ask blocks in
    /// different .tkc files store the value in different positions.
    /// </summary>
    private static int DetectPriceOffset(byte[] data)
    {
        double d0 = ReadDoubleAt(data, 0);
        double d1 = ReadDoubleAt(data, 8);
        bool d0Is = d0 >= 0.5 && d0 <= 100_000.0;
        bool d1Is = d1 >= 0.5 && d1 <= 100_000.0;
        if (d0Is && !d1Is) return 0;
        if (d1Is && !d0Is) return 8;

        int mid = ((data.Length / 2) / RecordStride) * RecordStride;
        if (mid + 16 <= data.Length)
        {
            double m0 = ReadDoubleAt(data, mid);
            double m1 = ReadDoubleAt(data, mid + 8);
            bool m0Is = m0 >= 0.5 && m0 <= 100_000.0;
            bool m1Is = m1 >= 0.5 && m1 <= 100_000.0;
            if (m0Is && !m1Is) return 0;
            if (m1Is && !m0Is) return 8;
        }

        return 0;
    }

    private static double ReadDoubleAt(byte[] data, int off)
    {
        if (off < 0 || off + 8 > data.Length) return 0;
        return BinaryPrimitives.ReadDoubleLittleEndian(data.AsSpan(off, 8));
    }

    private static List<TkcTick> DecodeTickStream(byte[] ts, byte[] bid, byte[] ask)
    {
        int n = Math.Min(ts.Length / RecordStride,
                Math.Min(bid.Length / RecordStride, ask.Length / RecordStride));
        if (n == 0) return new List<TkcTick>();

        int bidOff = DetectPriceOffset(bid);
        int askOff = DetectPriceOffset(ask);

        var result = new List<TkcTick>(n);

        long running = (long)BinaryPrimitives.ReadUInt64LittleEndian(ts.AsSpan(0, 8));

        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                ulong delta = BinaryPrimitives.ReadUInt64LittleEndian(ts.AsSpan(i * RecordStride, 8));
                running += (long)delta;
            }

            double b = BinaryPrimitives.ReadDoubleLittleEndian(bid.AsSpan(i * RecordStride + bidOff, 8));
            double a = BinaryPrimitives.ReadDoubleLittleEndian(ask.AsSpan(i * RecordStride + askOff, 8));

            result.Add(new TkcTick(running, b, a));
        }

        return result;
    }

    // ── Internals ───────────────────────────────────────────────────

    private readonly record struct TkcBlock(byte[] Data, TkcBlockType Type, int Size, int Offset);

    private enum TkcBlockType
    {
        Unknown,
        Timestamps,
        Prices,
        Empty,
    }
}