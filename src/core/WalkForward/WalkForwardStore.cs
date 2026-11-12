using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.WalkForward;

/// <summary>
/// Read/write for walk-forward batch archives at <c>data/walkforward/</c>.
/// Mirrors <c>OptimizationStore</c> 1:1 so a developer who knows one knows
/// the other, but with three differences worth documenting:
///
/// <list type="bullet">
///   <item><see cref="SaveAsync"/> writes to a <c>.tmp</c> file and then moves
///   it into place, so a crash mid-write leaves no half-JSON file.</item>
///   <item><see cref="LoadAsync"/> rejects files whose
///   <see cref="WalkForwardBatch.SchemaVersion"/> is newer than
///   <see cref="WalkForwardBatch.CurrentSchema"/>. Older versions are
///   accepted (post-v1 additions are nullable).</item>
///   <item><see cref="NewId"/> produces a sortable UTC timestamp with a
///   short random suffix, so <see cref="ListBatchFiles"/> can order by
///   filename and get chronological order.</item>
/// </list>
/// </summary>
public sealed class WalkForwardStore
{
    private const string Root = "data/walkforward";
    private const string Extension = ".json";

    public WalkForwardStore()
    {
        Directory.CreateDirectory(Root);
    }

    public string RootDirectory => Root;

    /// <summary>Returns a fresh, sort-friendly id: UTC timestamp + 4 hex chars.</summary>
    public string NewId()
    {
        Span<byte> buf = stackalloc byte[2];
        Random.Shared.NextBytes(buf);
        return DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff") + "-" + Convert.ToHexString(buf).ToLowerInvariant();
    }

    /// <summary>
    /// Writes the batch to <c>data/walkforward/wf_{Id}.json</c> atomically.
    /// Returns the full path.
    /// </summary>
    public async Task<string> SaveAsync(WalkForwardBatch batch, CancellationToken ct = default)
    {
        if (batch == null) throw new ArgumentNullException(nameof(batch));

        Directory.CreateDirectory(Root);

        var fileName = $"wf_{batch.Id}{Extension}";
        var finalPath = Path.Combine(Root, fileName);
        var tmpPath = finalPath + ".tmp";

        var sw = Stopwatch.StartNew();
        try
        {
            await using (var fs = new FileStream(
                tmpPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    fs,
                    batch,
                    WalkForwardJsonContext.Default.WalkForwardBatch,
                    ct).ConfigureAwait(false);
            }

            File.Move(tmpPath, finalPath, overwrite: true);

            sw.Stop();
            long size = 0;
            try { size = new FileInfo(finalPath).Length; } catch { }

            LogManager.Info(LogTag.Runner,
                $"walk-forward batch saved: {fileName} " +
                $"({batch.Windows.Length} window(s), {size / 1024.0:F0} KB, {sw.ElapsedMilliseconds} ms)");

            return finalPath;
        }
        catch (Exception ex)
        {
            TryDelete(tmpPath);
            LogManager.Error(LogTag.Runner, $"walk-forward batch save failed: {fileName}", ex);
            throw;
        }
    }

    /// <summary>
    /// Loads a batch. Returns null for a missing or empty file, for a file
    /// that cannot be parsed, or for a schema version this build does not
    /// understand. Warnings go to the Runner log; callers can treat null as
    /// "skip this file".
    /// </summary>
    public async Task<WalkForwardBatch?> LoadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        try
        {
            if (!File.Exists(path)) return null;

            var sw = Stopwatch.StartNew();
            await using var fs = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (fs.Length == 0) return null;

            var batch = await JsonSerializer.DeserializeAsync(
                fs,
                WalkForwardJsonContext.Default.WalkForwardBatch,
                ct).ConfigureAwait(false);

            sw.Stop();
            if (batch == null) return null;

            if (batch.SchemaVersion > WalkForwardBatch.CurrentSchema)
            {
                LogManager.Warn(LogTag.Runner,
                    $"walk-forward batch {Path.GetFileName(path)}: schema v{batch.SchemaVersion} " +
                    $"is newer than this build (v{WalkForwardBatch.CurrentSchema}); skipping.");
                return null;
            }

            LogManager.Debug(LogTag.Runner,
                $"walk-forward batch loaded: {Path.GetFileName(path)} " +
                $"({batch.Windows.Length} window(s), {sw.ElapsedMilliseconds} ms)");
            return batch;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Runner,
                $"walk-forward batch load failed: {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    /// <summary>
    /// Enumerates the batch archive files, newest-first. Cheap: no parsing,
    /// just directory enumeration plus an ordinal filename sort (which is
    /// chronological because ids start with a UTC timestamp).
    /// </summary>
    public IReadOnlyList<string> ListBatchFiles()
    {
        try
        {
            if (!Directory.Exists(Root)) return Array.Empty<string>();

            return Directory
                .EnumerateFiles(Root, "*" + Extension, SearchOption.TopDirectoryOnly)
                .OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Runner, "walk-forward batch enumeration failed", ex);
            return Array.Empty<string>();
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }
}