using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core;

public record StrategyDescriptor(
    string Path,
    string Name,
    DateTime LastWriteUtc,
    long Size
);

public class StrategyCatalogChangedEventArgs : EventArgs
{
    public IReadOnlyList<StrategyDescriptor> Strategies { get; }
    public StrategyCatalogChangedEventArgs(IReadOnlyList<StrategyDescriptor> strategies)
    {
        Strategies = strategies;
    }
}

public sealed class StrategyCatalogService : IDisposable
{
    private const string StrategiesFolder = "data/strategies";
    private const int DebounceMs = 250;

    private readonly object _gate = new();
    private readonly FileSystemWatcher? _watcher;
    private readonly Timer? _debounceTimer;
    private List<StrategyDescriptor> _current = new();
    private bool _disposed;

    public StrategyCatalogService()
    {
        Directory.CreateDirectory(StrategiesFolder);
        _current = Scan();

        try
        {
            _watcher = new FileSystemWatcher(StrategiesFolder, "*.cs")
            {
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };

            _watcher.Created += OnFsEvent;
            _watcher.Changed += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
            _watcher.Error   += OnWatcherError;

            _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);
        }
        catch (Exception)
        {
            // Watcher is best-effort; catalog still works via manual Refresh().
            _watcher = null;
            _debounceTimer = null;
        }
    }

    public IReadOnlyList<StrategyDescriptor> Current
    {
        get { lock (_gate) { return _current; } }
    }

    public event EventHandler<StrategyCatalogChangedEventArgs>? Changed;

    public void Refresh()
    {
        List<StrategyDescriptor> snapshot;
        lock (_gate)
        {
            _current = Scan();
            snapshot = _current;
        }
        RaiseChanged(snapshot);
    }

    public StrategyDescriptor? Find(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var full = System.IO.Path.GetFullPath(path);
        lock (_gate)
        {
            return _current.FirstOrDefault(s =>
                string.Equals(s.Path, full, StringComparison.OrdinalIgnoreCase));
        }
    }

    public Task<string> ReadSourceAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        return File.ReadAllTextAsync(path, ct);
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        _debounceTimer?.Change(DebounceMs, Timeout.Infinite);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Watcher buffer overflow or similar: force a full rescan.
        _debounceTimer?.Change(DebounceMs, Timeout.Infinite);
    }

    private void OnDebounceElapsed(object? state)
    {
        if (_disposed) return;
        Refresh();
    }

    private void RaiseChanged(IReadOnlyList<StrategyDescriptor> snapshot)
    {
        Changed?.Invoke(this, new StrategyCatalogChangedEventArgs(snapshot));
    }

    private static List<StrategyDescriptor> Scan()
    {
        try
        {
            if (!Directory.Exists(StrategiesFolder))
                Directory.CreateDirectory(StrategiesFolder);

            return Directory
                .EnumerateFiles(StrategiesFolder, "*.cs", SearchOption.TopDirectoryOnly)
                .Select(p =>
                {
                    var info = new FileInfo(p);
                    return new StrategyDescriptor(
                        Path: info.FullName,
                        Name: System.IO.Path.GetFileNameWithoutExtension(info.Name),
                        LastWriteUtc: info.LastWriteTimeUtc,
                        Size: info.Length);
                })
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<StrategyDescriptor>();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFsEvent;
            _watcher.Changed -= OnFsEvent;
            _watcher.Deleted -= OnFsEvent;
            _watcher.Renamed -= OnFsEvent;
            _watcher.Error   -= OnWatcherError;
            _watcher.Dispose();
        }

        _debounceTimer?.Dispose();
    }
}