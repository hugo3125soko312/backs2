using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace backs2.Core;

public sealed record LogEntry(DateTime TimestampUtc, string Level, string Message)
{
    public string Formatted =>
        $"{TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff} [{Level}] {Message}";
}

public class LogManager
{
    private const int RingBufferSize = 1000;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _recent = new(RingBufferSize);
    private readonly string _logDirectory = "logs";
    private readonly string _logFilePath;

    public LogManager()
    {
        Directory.CreateDirectory(_logDirectory);

        // One file per app run.
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        _logFilePath = Path.Combine(_logDirectory, $"runtime-{stamp}.log");

        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(
                _logFilePath,
                shared: true,
                flushToDiskInterval: TimeSpan.FromMilliseconds(500))
            .CreateLogger();
    }

    public event EventHandler<LogEntry>? Logged;

    public string CurrentLogFilePath => _logFilePath;

    public void Info(string message) => Write("INF", message);

    public void Error(string message) => Write("ERR", message);

    private void Write(string level, string message)
    {
        var entry = new LogEntry(DateTime.UtcNow, level, message);

        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > RingBufferSize)
                _recent.Dequeue();
        }

        if (level == "ERR")
            Log.Error(message);
        else
            Log.Information(message);

        Logged?.Invoke(this, entry);
    }

    // Snapshot of the in-memory ring buffer, oldest → newest.
    public IReadOnlyList<LogEntry> GetRecentEntries()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }

    // File-based fallback: reads the current run's file (last 100 lines, oldest → newest).
    public List<string> GetLatestLogs()
    {
        if (!File.Exists(_logFilePath))
            return new List<string>();

        try
        {
            using var stream = new FileStream(
                _logFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var reader = new StreamReader(stream);

            var all = new List<string>();
            string? line;
            while ((line = reader.ReadLine()) != null)
                all.Add(line);

            if (all.Count <= 100)
                return all;

            return all.Skip(all.Count - 100).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}