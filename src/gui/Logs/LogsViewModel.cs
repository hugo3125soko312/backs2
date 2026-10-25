using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;

namespace backs2.ViewModels;

public partial class LogsViewModel : ObservableObject, IDisposable
{
    private const int MaxDisplayLines = 2000;

    private readonly object _gate = new();
    private readonly Queue<string> _lines = new(MaxDisplayLines + 16);
    private bool _flushScheduled;
    private readonly DataManager _data;
    private readonly ConfigManager _config;
    private bool _disposed;

    /// <summary>
    /// True while the Logs view is on screen. Flushing rebuilds the entire
    /// LogText string (up to 2,000 lines) and rebinds the TextBox, so doing it
    /// while the user is watching a different tab is pure waste — and on the
    /// optimizer workload it was the main source of UI thread contention
    /// during a run. Lines keep accumulating in the queue regardless; a single
    /// catch-up flush runs when the view becomes visible again.
    /// </summary>
    private bool _isVisible;

    [ObservableProperty] private string _logText = "";
    [ObservableProperty] private string _commandInput = "";
    [ObservableProperty] private string _commandStatus = "";
    [ObservableProperty] private bool _isExecuting;

    public LogsViewModel(DataManager data, ConfigManager config)
    {
        _data = data;
        _config = config;

        foreach (var entry in LogManager.GetRecentEntries())
            AppendLine(FormatEntry(entry));

        Flush();

        LogManager.Logged += OnLogged;
    }

    /// <summary>
    /// Called by the view when it enters or leaves the visual tree. While
    /// detached, log lines accumulate but no flush is scheduled; on reattach a
    /// single flush delivers everything that arrived in the meantime.
    /// </summary>
    public void SetVisible(bool visible)
    {
        if (_isVisible == visible) return;
        _isVisible = visible;

        if (visible)
        {
            Dispatcher.UIThread.Post(Flush);
        }
    }

    private void OnLogged(object? sender, LogManager.LogEntry entry)
    {
        AppendLine(FormatEntry(entry));

        // One pending flush at a time. A burst of N log lines (the optimizer
        // emits thousands per run) used to post N dispatcher callbacks, each
        // rebuilding the whole LogText string. Now the burst coalesces into a
        // single post that snapshots whatever accumulated.
        // Nothing on screen to update — the queue keeps growing and
        // SetVisible(true) does one catch-up flush.
        if (!_isVisible) return;

        lock (_gate)
        {
            if (_flushScheduled) return;
            _flushScheduled = true;
        }

        Dispatcher.UIThread.Post(Flush);
    }

    private static string FormatEntry(LogManager.LogEntry entry)
    {
        var line = $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] [{entry.Tag}] {entry.Message}";

        if (entry.Exception == null) return line;

        var sb = new StringBuilder(line.Length + 128);
        sb.Append(line);
        sb.Append(" — ");
        sb.Append(entry.Exception.GetType().Name);
        sb.Append(": ");
        sb.Append(entry.Exception.Message);

        var inner = entry.Exception.InnerException;
        while (inner != null)
        {
            sb.Append(" → ");
            sb.Append(inner.GetType().Name);
            sb.Append(": ");
            sb.Append(inner.Message);
            inner = inner.InnerException;
        }

        return sb.ToString();
    }

    private void AppendLine(string line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxDisplayLines) _lines.Dequeue();
        }
    }

    private void Flush()
    {
        string snapshot;
        lock (_gate)
        {
            _flushScheduled = false;
            if (_lines.Count == 0) { return; }
            snapshot = string.Join(Environment.NewLine, _lines);
        }
        LogText = snapshot;
    }

    [RelayCommand]
    private void Refresh()
    {
        lock (_gate) { _lines.Clear(); }

        var path = LogManager.CurrentLogFilePath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    AppendLine(line);
            }
            catch (Exception ex)
            {
                AppendLine($"[ERR] [App] Failed to read log file: {ex.Message}");
            }
        }

        Flush();
    }

    [RelayCommand]
    private void ClearView()
    {
        lock (_gate) { _lines.Clear(); }
        LogText = "";
    }

    [RelayCommand]
    private async Task CopyAllAsync()
    {
        var text = LogText;
        if (string.IsNullOrEmpty(text)) return;

        var clipboard = Avalonia.Application.Current?.ApplicationLifetime is
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow?.Clipboard
            : null;

        if (clipboard != null)
            await clipboard.SetTextAsync(text);
    }

    // ------------------------------------------------------------------
    // Command line
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task ExecuteCommandAsync()
    {
        var raw = CommandInput?.Trim() ?? "";
        if (raw.Length == 0) return;

        CommandInput = "";
        IsExecuting = true;

        try
        {
            // Multiline paste: one command per line. Semicolons are also
            // accepted as separators so `a; b` on a single line works.
            var commands = raw.Split(
                new[] { '\r', '\n', ';' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in commands)
            {
                var cmd = line.Trim();
                if (cmd.Length == 0) continue;
                await RunSingleCommandAsync(cmd);
            }
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private async Task RunSingleCommandAsync(string cmd)
    {
        AppendLine($"> {cmd}");
        Flush();

        try
        {
            await DispatchCommandAsync(cmd);
        }
        catch (Exception ex)
        {
            // One bad command does not abort the rest of the batch.
            CommandStatus = $"Command failed: {ex.Message}";
            AppendLine($"[ERR] [App] Command failed — {ex.GetType().Name}: {ex.Message}");
            Flush();
        }
    }

    private async Task DispatchCommandAsync(string raw)
    {
        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();

        switch (verb)
        {
            case "help":
                EmitHelp();
                break;

            case "providers":
                EmitProviders();
                break;

            case "fetch":
                await CmdFetchAsync(parts);
                break;

            case "test":
                await CmdTestAsync(parts);
                break;

            case "clear":
                ClearView();
                break;

            default:
                CommandStatus = $"Unknown command: {verb}. Type 'help'.";
                break;
        }
    }

    private void EmitHelp()
    {
        AppendLine("Commands:");
        AppendLine("  help                                  — this message");
        AppendLine("  providers                             — list configured providers");
        AppendLine("  fetch [provider] <symbol> <timeframe> — fetch via DataManager. provider defaults to Auto");
        AppendLine("  test <provider> <symbol> <timeframe>  — call a specific provider directly (no cache)");
        AppendLine("  clear                                 — clear the log view");
        AppendLine("");
        AppendLine("Provider names may contain spaces (Twelve Data, FCS API).");
        AppendLine("Multiline paste executes sequentially — one command per line.");
        AppendLine("Examples:");
        AppendLine("  fetch XAUUSD 1D                — Auto walk");
        AppendLine("  fetch Yahoo XAUUSD 1D          — Yahoo preferred, fall through on failure");
        AppendLine("  test FCS API EURUSD 1h         — direct call, no cache, no fallback");
        Flush();
        CommandStatus = "help shown";
    }

    private void EmitProviders()
    {
        AppendLine("Configured providers:");
        foreach (var p in _config.Settings.ProviderOrder)
            AppendLine($"  - {p}");
        Flush();
        CommandStatus = $"{_config.Settings.ProviderOrder.Count} providers";
    }

    private async Task CmdFetchAsync(string[] parts)
    {
        if (parts.Length < 3)
        {
            CommandStatus = "Usage: fetch [provider] <symbol> <timeframe>";
            return;
        }

        string timeframe = parts[^1];
        string symbol = parts[^2];
        string providerName = parts.Length > 3
            ? string.Join(' ', parts[1..^2])
            : "";

        bool isAuto = string.IsNullOrWhiteSpace(providerName)
                   || string.Equals(providerName, "Auto", StringComparison.OrdinalIgnoreCase);

        string? preferred = isAuto ? null : providerName;
        string viaLabel = isAuto ? "Auto" : providerName;

        CommandStatus = $"fetching {symbol}@{timeframe} via {viaLabel}";
        AppendLine($"[CMD] fetch {symbol}@{timeframe} via {viaLabel}");
        Flush();

        var progress = new Progress<DataProgress>(p => CommandStatus = p.Message);
        var data = await _data.GetSymbolDataAsync(symbol, timeframe, progress, preferred);

        if (data == null || data.Count == 0)
        {
            AppendLine($"[CMD] fetch {symbol}@{timeframe} → empty");
            CommandStatus = "empty result";
        }
        else
        {
            var info = _data.GetCacheInfo(symbol, timeframe);
            string source = info?.Provider ?? "Unknown";

            string note;
            if (isAuto) note = "";
            else if (string.Equals(source, providerName, StringComparison.OrdinalIgnoreCase)) note = "";
            else note = $" (fell back from {providerName})";

            AppendLine($"[CMD] fetch {symbol}@{timeframe} → {data.Count} bars from {source}{note}");
            CommandStatus = $"{data.Count} bars from {source}{note}";
        }
        Flush();
    }

    private async Task CmdTestAsync(string[] parts)
    {
        if (parts.Length < 4)
        {
            CommandStatus = "Usage: test <provider> <symbol> <timeframe>";
            return;
        }

        string timeframe = parts[^1];
        string symbol = parts[^2];
        string providerName = string.Join(' ', parts[1..^2]);

        if (string.IsNullOrWhiteSpace(providerName))
        {
            CommandStatus = "test requires a specific provider";
            return;
        }

        if (string.Equals(providerName, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            CommandStatus = "test requires a specific provider, not Auto";
            return;
        }

        CommandStatus = $"testing {providerName} on {symbol}@{timeframe}";
        AppendLine($"[CMD] test {providerName} {symbol}@{timeframe}");
        Flush();

        var provider = _data.GetProvider(providerName);
        if (provider == null)
        {
            CommandStatus = $"provider '{providerName}' not registered";
            AppendLine($"[ERR] [CMD] provider '{providerName}' not registered");
            Flush();
            return;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await provider.FetchAsync(symbol, timeframe);
        sw.Stop();

        int bars = result?.Count ?? 0;

        if (bars == 0)
        {
            AppendLine($"[CMD] test {providerName} {symbol}@{timeframe} → 0 bars in {sw.ElapsedMilliseconds} ms");
            CommandStatus = $"0 bars in {sw.ElapsedMilliseconds} ms";
            Flush();
            return;
        }

        int nonZero = 0;
        long maxVol = 0;
        long totalVol = 0;
        for (int i = 0; i < result!.Count; i++)
        {
            long v = result[i].Volume;
            totalVol += v;
            if (v > 0) nonZero++;
            if (v > maxVol) maxVol = v;
        }

        string volTag = nonZero == 0 ? "NO VOLUME"
                      : nonZero == bars ? "full volume"
                      : $"partial {nonZero}/{bars}";

        AppendLine($"[CMD] test {providerName} {symbol}@{timeframe} → {bars} bars in {sw.ElapsedMilliseconds} ms");
        AppendLine($"[CMD]   volume: {volTag} — nonzero {nonZero}/{bars}, max {maxVol}, total {totalVol}");
        CommandStatus = $"{bars} bars, {volTag}";
        Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LogManager.Logged -= OnLogged;
    }
}