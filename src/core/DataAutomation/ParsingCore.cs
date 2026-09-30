using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using backs2.Core;

namespace backs2.Core.DataAutomation;

#region Records

public sealed record ParseRequest(
    string Symbol,
    string Timeframe,
    bool UseGpu);

public sealed record ParseResult(
    int TotalShots,
    int ParsedShots,
    int FailedShots,
    string OutputPath);

#endregion

#region OCR parsing

public sealed class ShotParsingService
{
    private static readonly Regex NumberRegex = new(
        @"([\d,]+\.?\d*)\s*([KMB]?)",
        RegexOptions.Compiled);

    private static readonly Regex DateRegex = new(
        @"(\d{1,2})[/.](\d{1,2})[/.](\d{2,4})",
        RegexOptions.Compiled);

    private static readonly Regex TimeRegex = new(
        @"(\d{1,2}):(\d{2})(?::(\d{2}))?",
        RegexOptions.Compiled);

    private const int StepBatchSize = 64;
    private const int MaxLoggedMisses = 200;

    private const double MaxIntraBarRange = 0.05;
    private const double MinVolume = 100.0;

    private const long TimestampLowerBoundMs = 30L * 24 * 60 * 60 * 1000;

    private static readonly string[] NumericFields = { "O", "H", "L", "C", "V" };
    private static readonly string[] AllFields = { "O", "H", "L", "C", "V", "DATE", "TIME" };

    public async Task<ParseResult> ParseAsync(
        ParseRequest request,
        IProgress<(int Current, int Total)> progress,
        CancellationToken ct)
    {
        LogEnvironmentInfo();

        var shotsDir = Path.Combine("data", "shots");
        if (!Directory.Exists(shotsDir))
            throw new DirectoryNotFoundException($"Shots directory not found: {shotsDir}");

        var prefix = $"{request.Symbol}_{request.Timeframe}_";

        var oFiles = Directory.GetFiles(shotsDir, $"{prefix}*_O.png")
                              .Select(f => (Path: f, Step: ExtractStepIndex(f, prefix)))
                              .Where(x => x.Step >= 0)
                              .OrderBy(x => x.Step)
                              .Select(x => x.Path)
                              .ToArray();

        if (oFiles.Length == 0)
            throw new FileNotFoundException(
                $"No step files found for {request.Symbol}@{request.Timeframe} in {shotsDir}.");

        var steps = new string[oFiles.Length];
        for (int i = 0; i < oFiles.Length; i++)
        {
            var name = Path.GetFileNameWithoutExtension(oFiles[i]);
            steps[i] = name.Substring(0, name.Length - 2);
        }

        var modelDir = Path.Combine(AppContext.BaseDirectory, "models", "v5");
        var recPath = Path.Combine(modelDir, "latin_PP-OCRv5_rec_mobile_infer.onnx");
        var dictPath = Path.Combine(modelDir, "ppocrv5_latin_dict.txt");

        if (!File.Exists(recPath) || !File.Exists(dictPath))
            throw new FileNotFoundException(
                $"Recognizer model or dict missing under {modelDir}.");

        LogManager.Info(LogTag.Data,
            $"Parsing {steps.Length} step(s) for {request.Symbol}@{request.Timeframe}, " +
            $"GPU={(request.UseGpu ? "on" : "off")}, batch={StepBatchSize}");

        using var recognizer = new BatchedOcrRecognizer(recPath, dictPath, request.UseGpu);

        long timeframeMs = TimeframeToMs(request.Timeframe);
        long fallbackBaseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var fieldTexts = new Dictionary<string, string[]>(AllFields.Length);
        foreach (var f in AllFields)
            fieldTexts[f] = new string[steps.Length];

        int totalBatches = (steps.Length + StepBatchSize - 1) / StepBatchSize;
        int batchesDone = 0;
        int retriedCrops = 0;
        int retryRecovered = 0;

        await Task.Run(() =>
        {
            for (int batchStart = 0; batchStart < steps.Length; batchStart += StepBatchSize)
            {
                ct.ThrowIfCancellationRequested();

                int batchEnd = Math.Min(batchStart + StepBatchSize, steps.Length);
                int count = batchEnd - batchStart;

                var cropsByField = new Dictionary<string, SKBitmap?[]>(AllFields.Length);
                foreach (var f in AllFields)
                {
                    var arr = new SKBitmap?[count];
                    for (int j = 0; j < count; j++)
                    {
                        var path = Path.Combine(shotsDir, $"{steps[batchStart + j]}_{f}.png");
                        arr[j] = File.Exists(path) ? SKBitmap.Decode(path) : null;
                    }
                    cropsByField[f] = arr;
                }

                try
                {
                    foreach (var f in AllFields)
                    {
                        var crops = cropsByField[f];
                        var texts = recognizer.RecognizeBatch(crops);
                        for (int j = 0; j < count; j++)
                            fieldTexts[f][batchStart + j] = texts[j] ?? "";
                    }

                    var retryKeys = new List<(string Field, int Index)>();
                    for (int j = 0; j < count; j++)
                    {
                        foreach (var f in AllFields)
                        {
                            if (string.IsNullOrWhiteSpace(fieldTexts[f][batchStart + j]))
                                retryKeys.Add((f, batchStart + j));
                        }
                    }

                    if (retryKeys.Count > 0)
                    {
                        retriedCrops += retryKeys.Count;

                        var retryCrops = new SKBitmap?[retryKeys.Count];
                        for (int k = 0; k < retryKeys.Count; k++)
                        {
                            var (f, idx) = retryKeys[k];
                            var src = cropsByField[f][idx - batchStart];
                            retryCrops[k] = src == null ? null : PadAndUpscale(src, 2, 2.0f);
                        }

                        try
                        {
                            var retryTexts = recognizer.RecognizeBatch(retryCrops);
                            for (int k = 0; k < retryKeys.Count; k++)
                            {
                                var (f, idx) = retryKeys[k];
                                var t = retryTexts[k];
                                if (!string.IsNullOrWhiteSpace(t))
                                {
                                    fieldTexts[f][idx] = t;
                                    retryRecovered++;
                                }
                            }
                        }
                        finally
                        {
                            foreach (var b in retryCrops) b?.Dispose();
                        }
                    }
                }
                finally
                {
                    foreach (var arr in cropsByField.Values)
                        foreach (var b in arr) b?.Dispose();
                }

                batchesDone++;
                progress.Report((batchesDone, totalBatches));
            }
        }, ct);

        if (retriedCrops > 0)
        {
            LogManager.Info(LogTag.Data,
                $"[OCR] Retry pass: {retryRecovered}/{retriedCrops} crop(s) recovered " +
                $"(padded 2px, upscaled 2x)");
        }

        // ── Timestamp derivation pass ────────────────────────────────
        // Runs over every raw step, kept or dropped. The derived walk
        // advances 1 slot per raw step, not 1 slot per kept bar.
        var timestamps = new long[steps.Length];
        int dateOcrHits = 0;
        int dateOcrMisses = 0;
        {
            long? prev = null;
            for (int i = 0; i < steps.Length; i++)
            {
                long? fromOcr = null;
                {
                    var dateText = fieldTexts["DATE"][i] ?? "";
                    var timeText = fieldTexts["TIME"][i] ?? "";
                    var d = ParseDate(dateText);
                    var ts = ParseTime(timeText);

                    if (d.HasValue && ts.HasValue)
                    {
                        var combined = d.Value.Date + ts.Value;
                        var candidate = new DateTimeOffset(
                            DateTime.SpecifyKind(combined, DateTimeKind.Utc),
                            TimeSpan.Zero).ToUnixTimeMilliseconds();

                        bool good = prev == null
                            || (candidate < prev.Value
                                && candidate > prev.Value - TimestampLowerBoundMs);
                        if (good) fromOcr = candidate;
                    }
                }

                long tsMs;
                if (fromOcr.HasValue) { tsMs = fromOcr.Value; dateOcrHits++; }
                else
                {
                    dateOcrMisses++;
                    tsMs = prev.HasValue
                        ? prev.Value - timeframeMs
                        : fallbackBaseTime - (long)i * timeframeMs;
                }

                timestamps[i] = tsMs;
                prev = tsMs;
            }
        }

        // ── Parse loop ───────────────────────────────────────────────
        var parsed = new List<ParsedKline>(steps.Length);
        var misses = new List<int>();

        int ocrMisses = 0;
        int volFloorRejects = 0;
        int ohlcRejects = 0;
        int oRecovered = 0;
        int volumeRecovered = 0;
        int logMisses = 0;

        double? lastParsedClose = null;

        for (int i = 0; i < steps.Length; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var nums = new double[5];
                bool[] ok = new bool[5];

                for (int f = 0; f < NumericFields.Length; f++)
                {
                    var field = NumericFields[f];
                    var text = fieldTexts[field][i] ?? "";

                    if (!TryParseSingleNumber(text, out double parsedValue, out string suffix))
                    {
                        if (logMisses < MaxLoggedMisses)
                        {
                            LogManager.Warn(LogTag.Data,
                                $"OCR miss on {steps[i]}_{field}: '{text}'");
                            logMisses++;
                        }
                        continue;
                    }

                    if (field == "V")
                    {
                        bool volHadSuffix = !string.IsNullOrEmpty(suffix);
                        parsedValue = Math.Round(parsedValue * SuffixMultiplier(suffix),
                                                 MidpointRounding.AwayFromZero);

                        if (!volHadSuffix && parsedValue > 0 && parsedValue < 1000)
                        {
                            double rec = parsedValue * 1000.0;
                            if (rec >= MinVolume) { parsedValue = rec; volumeRecovered++; }
                        }
                    }
                    nums[f] = parsedValue;
                    ok[f] = true;
                }

                if (!ok[0] && ok[1] && ok[2])
                {
                    double hiB = nums[1], loB = nums[2];
                    double anchor = lastParsedClose ?? (loB + hiB) * 0.5;
                    nums[0] = Math.Min(Math.Max(anchor, loB), hiB);
                    ok[0] = true;
                    oRecovered++;
                }

                if (!ok[0] || !ok[1] || !ok[2] || !ok[3])
                {
                    ocrMisses++;
                    misses.Add(i);
                    continue;
                }

                if (nums[4] < MinVolume)
                {
                    volFloorRejects++;
                    misses.Add(i);
                    if (logMisses < MaxLoggedMisses)
                    {
                        LogManager.Warn(LogTag.Data,
                            $"Volume below floor on {steps[i]}: V={nums[4]}");
                        logMisses++;
                    }
                    continue;
                }

                double o = nums[0], h = nums[1], l = nums[2], c = nums[3], v = nums[4];

                var k = new ParsedKline(timestamps[i], o, h, l, c, v);
                if (!IsOhlcSane(k))
                {
                    ohlcRejects++;
                    misses.Add(i);
                    if (logMisses < MaxLoggedMisses)
                    {
                        LogManager.Warn(LogTag.Data,
                            $"OHLC reject on {steps[i]}: O={k.O} H={k.H} L={k.L} C={k.C}");
                        logMisses++;
                    }
                }
                else
                {
                    parsed.Add(k);
                    lastParsedClose = k.C;
                }
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.Data, $"Step {i} failed", ex);
                ocrMisses++;
                misses.Add(i);
            }
        }

        parsed.Reverse();

        var outDir = Path.Combine("data", "parsed");
        Directory.CreateDirectory(outDir);

        string baseName = $"{request.Symbol}_{request.Timeframe}";
        string outPath;
        int n2 = 1;
        while (true)
        {
            outPath = Path.Combine(outDir, $"{baseName}_{n2}.json");
            if (!File.Exists(outPath)) break;
            n2++;
        }

        var json = JsonSerializer.Serialize(parsed,
            new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outPath, json, ct);

        // Write the misses list alongside the parsed output. One step index
        // per line, ascending. Feed this into the Data Automation tab's
        // "Re-extract Missing" input on the next pass.
        string missesPath = Path.Combine(outDir, $"{baseName}_misses.txt");
        if (misses.Count > 0)
        {
            misses.Sort();
            await File.WriteAllLinesAsync(missesPath, misses.Select(i => i.ToString()), ct);
            LogManager.Info(LogTag.Data,
                $"Wrote {misses.Count} missing step index(s) to {missesPath}");
        }
        else if (File.Exists(missesPath))
        {
            try { File.Delete(missesPath); } catch { }
        }

        int totalFailed = ocrMisses + volFloorRejects + ohlcRejects;

        LogManager.Info(LogTag.Data,
            $"Parsed {parsed.Count}/{steps.Length} step(s) → {outPath} " +
            $"(ocr miss={ocrMisses}, O recovered={oRecovered}, " +
            $"vol floor={volFloorRejects}, OHLC reject={ohlcRejects}, " +
            $"vol×K recovered={volumeRecovered}, " +
            $"retry recovered={retryRecovered}/{retriedCrops}, " +
            $"date/timestamp: {dateOcrHits} OCR, {dateOcrMisses} derived)");

        return new ParseResult(steps.Length, parsed.Count, totalFailed, outPath);
    }

    private static SKBitmap PadAndUpscale(SKBitmap src, int padPx, float scale)
    {
        int newW = (int)Math.Ceiling(src.Width * scale) + padPx * 2;
        int newH = (int)Math.Ceiling(src.Height * scale) + padPx * 2;
        if (newW < 1) newW = 1;
        if (newH < 1) newH = 1;

        var dst = new SKBitmap(newW, newH, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.Black);

#pragma warning disable CS0618
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };
        canvas.DrawBitmap(
            src,
            new SKRect(0, 0, src.Width, src.Height),
            new SKRect(padPx, padPx, padPx + src.Width * scale, padPx + src.Height * scale),
            paint);
#pragma warning restore CS0618
        return dst;
    }

    private static int ExtractStepIndex(string path, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) return -1;

        var tail = name.Substring(prefix.Length);
        var underscore = tail.IndexOf('_');
        if (underscore <= 0) return -1;

        var digits = tail.Substring(0, underscore);
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n : -1;
    }

    private static bool TryParseSingleNumber(string text, out double value, out string suffix)
    {
        value = 0;
        suffix = "";
        if (string.IsNullOrWhiteSpace(text)) return false;

        var m = NumberRegex.Match(text);
        if (!m.Success) return false;

        var cleaned = m.Groups[1].Value.Replace(",", "");
        if (!double.TryParse(cleaned,
            NumberStyles.Any, CultureInfo.InvariantCulture, out value)) return false;

        suffix = m.Groups[2].Value;
        return true;
    }

    private static DateTime? ParseDate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = DateRegex.Match(text);
        if (!m.Success) return null;

        if (!int.TryParse(m.Groups[1].Value, out int a)) return null;
        if (!int.TryParse(m.Groups[2].Value, out int b)) return null;
        if (!int.TryParse(m.Groups[3].Value, out int y)) return null;

        if (y < 100) y += 2000;

        int day, month;
        if (a > 12 && b <= 12)      { day = a; month = b; }
        else if (b > 12 && a <= 12) { day = b; month = a; }
        else                        { day = a; month = b; }

        if (day < 1 || day > 31 || month < 1 || month > 12) return null;

        try { return new DateTime(y, month, day); }
        catch { return null; }
    }

    private static TimeSpan? ParseTime(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = TimeRegex.Match(text);
        if (!m.Success) return null;

        if (!int.TryParse(m.Groups[1].Value, out int h)) return null;
        if (!int.TryParse(m.Groups[2].Value, out int min)) return null;
        int sec = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out int s) ? s : 0;

        if (h < 0 || h > 23 || min < 0 || min > 59 || sec < 0 || sec > 59) return null;
        return new TimeSpan(h, min, sec);
    }

    private static bool IsOhlcSane(ParsedKline k)
    {
        if (k.H < k.L) return false;
        if (k.O > k.H || k.O < k.L) return false;
        if (k.C > k.H || k.C < k.L) return false;
        if (k.V < MinVolume) return false;
        if (k.O <= 0 || k.H <= 0 || k.L <= 0 || k.C <= 0) return false;

        double range = (k.H - k.L) / k.H;
        if (range > MaxIntraBarRange) return false;

        return true;
    }

    private static void LogEnvironmentInfo()
    {
        try
        {
            var ortManaged = typeof(Microsoft.ML.OnnxRuntime.InferenceSession)
                .Assembly.GetName();
            LogManager.Info(LogTag.Data, $"Environment: ORT-managed={ortManaged.Version}");

            try
            {
                string native = Microsoft.ML.OnnxRuntime.OrtEnv.Instance()
                    .GetVersionString();
                LogManager.Info(LogTag.Data, $"ORT-native={native}");
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.Data, "Could not query ORT native version.", ex);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Data, "Environment info dump failed.", ex);
        }
    }

    private static double SuffixMultiplier(string suffix) => suffix.ToUpperInvariant() switch
    {
        "K" => 1_000,
        "M" => 1_000_000,
        "B" => 1_000_000_000,
        _   => 1
    };

    private static long TimeframeToMs(string tf) => tf switch
    {
        "1m"  => 60_000L,
        "2m"  => 2 * 60_000L,
        "3m"  => 3 * 60_000L,
        "5m"  => 5 * 60_000L,
        "15m" => 15 * 60_000L,
        "30m" => 30 * 60_000L,
        "45m" => 45 * 60_000L,
        "1h"  => 3_600_000L,
        "2h"  => 2 * 3_600_000L,
        "3h"  => 3 * 3_600_000L,
        "4h"  => 4 * 3_600_000L,
        "1D"  => 86_400_000L,
        "1W"  => 7 * 86_400_000L,
        _     => 86_400_000L
    };

    public sealed record ParsedKline(long T, double O, double H, double L, double C, double V);
}

#endregion