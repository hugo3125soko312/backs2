using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

    public sealed record LogEntry(
        DateTime Timestamp,
        string Level,
        LogTag Tag,
        string Message,
        Exception? Exception = null);

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
            message,
            ex);

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

    /// <summary>
    /// Per-run duplicate suppression for the compute path (ROADMAP Phase 29B.2).
    ///
    /// <para><b>The rule.</b> Within one run, the first occurrence of a message is
    /// written at its own severity — a failure keeps <c>[ERR]</c> and its exception
    /// detail — and every later occurrence of the <em>same text</em> is suppressed.
    /// Nothing is downgraded and nothing is silenced outright: a genuinely novel
    /// message differs in text, so it always gets its own first line. This exists
    /// because the compute path fans one failure out across every worker and every
    /// chunk, which turned a single GPU pipeline-cache collision into eight
    /// identical stack traces per run.</para>
    ///
    /// <para><b>Key.</b> The message text is the whole key. It carries the tag
    /// prefix where callers include one, and two distinct events never share an
    /// identical message — so callers that interpolate the varying part (chunk
    /// index, device name) get one line per distinct event for free.</para>
    ///
    /// <para><b>Thread safety.</b> The registry is a
    /// <see cref="ConcurrentDictionary{TKey,TValue}"/> and the counters use
    /// <see cref="Interlocked"/>, because the compute path logs from every
    /// scheduler worker at once. Contention is a non-issue: the dictionary is
    /// only touched on the error/fallback path, never on the success path.</para>
    /// </summary>
    public static class LogOnce
    {
        private static readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);

        private static long _fallbacks;
        private static long _dispatches;
        private static long _fallbacksAtLastFlush;
        private static string? _firstFailure;

        /// <summary>
        /// Writes <paramref name="message"/> only if this run has not already
        /// written that exact text. Returns true when the line was written, so a
        /// caller can distinguish "reported" from "suppressed" without guessing.
        /// </summary>
        public static bool Warn(LogTag tag, string message) => First(_seen, message) && Emit(LogEventLevel.Warning, tag, message, null);

        /// <inheritdoc cref="Warn"/>
        public static bool Error(LogTag tag, string message, Exception? ex = null) => First(_seen, message) && Emit(LogEventLevel.Error, tag, message, ex);

        /// <inheritdoc cref="Warn"/>
        public static bool DebugMsg(LogTag tag, string message) => First(_seen, message) && Emit(LogEventLevel.Debug, tag, message, null);

        /// <summary>
        /// True the first time <paramref name="message"/> is seen this run.
        /// </summary>
        public static bool First(string message) => First(_seen, message);

        /// <summary>
        /// Records that one more Monte Carlo chunk ran on <paramref name="backend"/>
        /// because the device failed. <paramref name="detail"/> is the
        /// <c>TypeName: Message</c> of the first failure only; later calls are
        /// counted and their detail discarded.
        /// </summary>
        public static void RecordFallback(string detail)
        {
            Interlocked.Increment(ref _fallbacks);
            Interlocked.CompareExchange(ref _firstFailure, detail, null);
        }

        /// <summary>
        /// Records that one more Monte Carlo dispatch was scheduled against the
        /// GPU backend, whether or not any chunk of it fell back.
        /// </summary>
        public static void RecordDispatch() => Interlocked.Increment(ref _dispatches);

        /// <summary>
        /// Emits the run's one fallback summary — <c>N of M chunks fell back to
        /// CPU. First failure: ...</c> — and then nothing further until the next
        /// run. Called at the end of every dispatch; the counter check makes the
        /// first call after new fallbacks the one that reports, and the
        /// <see cref="First(string)"/> guard collapses the concurrent sweep of
        /// identical summaries that a parallel batch produces into a single line.
        /// </summary>
        public static void FlushFallbackSummary()
        {
            long fallbacks = Interlocked.Read(ref _fallbacks);
            long dispatches = Interlocked.Read(ref _dispatches);

            if (fallbacks == 0) return;
            if (fallbacks == Interlocked.Read(ref _fallbacksAtLastFlush)) return;

            Interlocked.Exchange(ref _fallbacksAtLastFlush, fallbacks);

            string detail = _firstFailure ?? "unknown";
            string summary =
                $"compute: {fallbacks} of {dispatches} Monte Carlo dispatch(es) fell back to the CPU " +
                $"(every chunk of each). First failure: {detail}";

            if (First(_seen, summary))
                Emit(LogEventLevel.Error, LogTag.MonteCarlo, summary, null);
        }

        /// <summary>
        /// Number of messages suppressed since the last <see cref="Reset"/> —
        /// the "how much spam did this remove" figure the phase report cites.
        /// </summary>
        public static long Suppressed => Interlocked.Read(ref _suppressed);

        /// <summary>
        /// Number of GPU dispatches whose chunks all fell back to the CPU — the
        /// denominator of the summary line.
        /// </summary>
        public static long Fallbacks => Interlocked.Read(ref _fallbacks);

        private static long _suppressed;

        /// <summary>
        /// Starts a new run: forgets every seen message and clears the counters,
        /// so the next run reports its own first occurrences again.
        /// </summary>
        public static void Reset()
        {
            _seen.Clear();
            Interlocked.Exchange(ref _fallbacks, 0);
            Interlocked.Exchange(ref _dispatches, 0);
            Interlocked.Exchange(ref _fallbacksAtLastFlush, 0);
            Interlocked.Exchange(ref _suppressed, 0);
            Interlocked.Exchange(ref _firstFailure, null);
        }

        private static bool First(ConcurrentDictionary<string, byte> seen, string message)
        {
            if (seen.TryAdd(message, 0))
            {
                return true;
            }

            Interlocked.Increment(ref _suppressed);
            return false;
        }

        private static bool Emit(LogEventLevel level, LogTag tag, string message, Exception? ex)
        {
            Write(level, tag, message, ex);
            return true;
        }
    }
}