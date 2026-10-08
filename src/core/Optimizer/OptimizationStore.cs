using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.Optimizer;

/// <summary>
/// Read/write for optimization batch archives at data/optimizations/.
/// One file per batch. Directory auto-created on construction, matching
/// the CacheManager / AccountService pattern.
///
/// Save is async and file-locked; load is async. ListBatchFiles is
/// synchronous — it only enumerates the directory, no parsing.
/// </summary>
public sealed class OptimizationStore
{
    private const string Root = "data/optimizations";
    private const string Extension = ".json";

    public OptimizationStore()
    {
        Directory.CreateDirectory(Root);
    }

    public string RootDirectory => Root;

    /// <summary>
    /// Writes the batch to data/optimizations/batch_{Id}.json. Returns
    /// the full path. Id collisions are resolved by appending a suffix.
    /// </summary>
    public async Task<string> SaveAsync(OptimizationBatch batch, CancellationToken ct = default)
    {
        if (batch == null) throw new ArgumentNullException(nameof(batch));

        var path = ResolveUniquePath(batch.Id);
        var sw = Stopwatch.StartNew();

        try
        {
            await using var fs = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous);

            await JsonSerializer.SerializeAsync(
                fs,
                batch,
                OptimizationJsonContext.Default.OptimizationBatch,
                ct).ConfigureAwait(false);

            sw.Stop();
            long sizeBytes = 0;
            try { sizeBytes = new FileInfo(path).Length; } catch { }

            LogManager.Info(LogTag.Runner,
                $"optimization batch saved: {Path.GetFileName(path)} " +
                $"({batch.Versions.Length} version(s), {sizeBytes / 1024.0:F0} KB, {sw.ElapsedMilliseconds} ms)");

            return path;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Runner, $"optimization batch save failed: {Path.GetFileName(path)}", ex);
            throw;
        }
    }

    public async Task<OptimizationBatch?> LoadAsync(string path, CancellationToken ct = default)
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
                OptimizationJsonContext.Default.OptimizationBatch,
                ct).ConfigureAwait(false);

            sw.Stop();
            if (batch != null)
            {
                LogManager.Debug(LogTag.Runner,
                    $"optimization batch loaded: {Path.GetFileName(path)} " +
                    $"({batch.Versions.Length} version(s), {sw.ElapsedMilliseconds} ms)");
            }
            return batch;
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Runner, $"optimization batch load failed: {Path.GetFileName(path)}", ex);
            return null;
        }
    }

    /// <summary>
    /// Enumerates the batch archive files, newest first. Cheap — no
    /// parsing, just directory enumeration and filename sort.
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
            LogManager.Error(LogTag.Runner, "optimization batch enumeration failed", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Resolves a unique filename for the given batch id. Collisions only
    /// happen if two batches start within the same second — appends a
    /// numeric suffix rather than overwriting.
    /// </summary>
    private static string ResolveUniquePath(string id)
    {
        var safeId = string.IsNullOrWhiteSpace(id)
            ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            : id;

        var basePath = Path.Combine(Root, $"batch_{safeId}{Extension}");
        if (!File.Exists(basePath)) return basePath;

        for (int n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(Root, $"batch_{safeId}_{n}{Extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        // Pathological fallback — timestamp to the millisecond.
        return Path.Combine(Root, $"batch_{DateTime.UtcNow:yyyyMMdd-HHmmssfff}{Extension}");
    }
}