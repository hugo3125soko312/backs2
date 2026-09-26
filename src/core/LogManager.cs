using System;
using System.Collections.Generic;
using System.IO;
using Serilog;
using Serilog.Events;

namespace backs2.Core;

public enum LogTag
{
    App, Cache, Data, Provider, Strategy, Runner,
    Account, MonteCarlo, Overview, Chart, Config, Metric
}

public static class LogManager
{
    private const int RingBufferCapacity = 1000;

    private static readonly object _gate = new();
    private static readonly Queue<LogEntry> _recent = new(RingBufferCapacity);

    private static ILogger? _logger;

    public static string CurrentLogFilePath { get; private set; } = "";

    public sealed record LogEntry(DateTime Timestamp, string Level, LogTag Tag, string Message);

    public static event EventHandler<LogEntry>? Logged;

    public static IReadOnlyList<LogEntry> GetRecentEntries()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }

    public static void Initialize(string minimumLevel = "Info")
    {
        lock (_gate)
        {
            if (_logger != null) return;

            Directory.CreateDirectory("logs");
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            CurrentLogFilePath = Path.Combine("logs", $"runtime-{stamp}.log");

            _logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    CurrentLogFilePath,
                    outputTemplate:
                        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{Tag}] {Message:lj}{NewLine}{Exception}",
                    flushToDiskInterval: TimeSpan.FromMilliseconds(500))
                .CreateLogger();
        }

        Info(LogTag.App, $"LogManager initialized → {CurrentLogFilePath}");
    }

    public static void SetMinimumLevel(string level)
    {
        // Filtering disabled — every log line is written and displayed.
        // Kept for API compatibility with App.axaml.cs.
    }

    public static void Shutdown()
    {
        ILogger? logger;
        lock (_gate)
        {
            logger = _logger;
            _logger = null;
        }

        if (logger == null) return;

        try { logger.ForContext("Tag", "App").Information("LogManager shutdown"); } catch { }
        (logger as IDisposable)?.Dispose();
    }

    public static void Info(LogTag tag, string message)
        => Write(LogEventLevel.Information, tag, message, null);

    public static void Warn(LogTag tag, string message)
        => Write(LogEventLevel.Warning, tag, message, null);

    public static void Debug(LogTag tag, string message)
        => Write(LogEventLevel.Debug, tag, message, null);

    public static void Error(LogTag tag, string message, Exception? ex = null)
        => Write(LogEventLevel.Error, tag, message, ex);

    private static void Write(LogEventLevel level, LogTag tag, string message, Exception? ex)
    {
        var entry = new LogEntry(
            DateTime.Now,
            LevelCode(level),
            tag,
            message);

        lock (_gate)
        {
            _recent.Enqueue(entry);
            while (_recent.Count > RingBufferCapacity)
                _recent.Dequeue();
        }

        var logger = _logger;
        if (logger != null)
        {
            try
            {
                var ctx = logger.ForContext("Tag", tag.ToString());
                if (ex != null) ctx.Write(level, ex, message);
                else ctx.Write(level, message);
            }
            catch
            {
                // File writer failed — swallow. Ring buffer still has the entry.
            }
        }

        Logged?.Invoke(null, entry);
    }

    private static string LevelCode(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose     => "VRB",
        LogEventLevel.Debug       => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning     => "WRN",
        LogEventLevel.Error       => "ERR",
        LogEventLevel.Fatal       => "FTL",
        _                         => "???"
    };
}