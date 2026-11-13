namespace backs2.ViewModels;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;

public partial class LogsViewModel : ObservableObject, IDisposable
{
    private const int MaxDisplayLines = 500;
    private const int MinFlushIntervalMs = 250;

    private long _lastFlushTicks;
    private System.Threading.Timer? _trailingFlush;
    private readonly object _gate = new();
    private readonly Queue<string> _lines = new(MaxDisplayLines + 16);
    private bool _flushScheduled;
    private readonly DataManager _data;
    private readonly ConfigManager _config;
    private bool _disposed;
    private bool _isVisible;

    [ObservableProperty] private string? _logText;
    [ObservableProperty] private string? _commandInput;
    [ObservableProperty] private string? _commandStatus;
    [ObservableProperty] private bool _isExecuting;

    public LogsViewModel(DataManager data, ConfigManager config)
    {
        _data = data;
        _config = config;
        LogManager.Logged += OnLogged;
    }

    public void SetVisible(bool visible)
    {
        if (_isVisible == visible) return;
        _isVisible = visible;

        if (visible)
        {
            Flush();
        }
    }

    private void OnLogged(object? sender, LogManager.LogEntry entry)
    {
        var line = FormatEntry(entry);

        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxDisplayLines) _lines.Dequeue();
        }

        if (_isVisible)
        {
            ScheduleFlush();
        }
    }

    private static string FormatEntry(LogManager.LogEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append($"[{entry.Timestamp:HH:mm:ss.fff}] ");
        sb.Append(entry.Level.ToString());
        sb.Append($" [{entry.Tag}] ");
        sb.Append(entry.Message);
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
        string text;
        lock (_gate)
        {
            if (_lines.Count == 0) return;
            var sb = new StringBuilder();
            foreach (var line in _lines)
                sb.AppendLine(line);
            text = sb.ToString();
        }
        LogText = text;
    }

    private void ScheduleFlush()
    {
        if (_flushScheduled) return;
        _flushScheduled = true;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        long interval = (long)(MinFlushIntervalMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);
        long due = now + interval;

        _trailingFlush?.Dispose();
        _trailingFlush = new System.Threading.Timer(_ =>
        {
            _flushScheduled = false;
            _trailingFlush = null;
            System.Windows.Application.Current.Dispatcher.Invoke(Flush);
        }, null, (int)(interval / System.Diagnostics.Stopwatch.Frequency * 1000), System.Threading.Timeout.Infinite);
    }

    [RelayCommand]
    private void Refresh()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var text = Array.Empty<string>(); // LoadLogFile stub
            sw.Stop();

            lock (_gate) { _lines.Clear(); }
            foreach (var line in text)
            {
                _lines.Enqueue(line);
                while (_lines.Count > MaxDisplayLines) _lines.Dequeue();
            }

            LogText = string.Join("\n", _lines);
        }
        catch (Exception ex)
        {
            LogText = $"Failed to read log file: {ex.Message}";
        }
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
        var text = LogText ?? "";
        try
        {
            System.Windows.Clipboard.SetText(text);
            CommandStatus = $"Copied {text.Length:N0} chars to clipboard";
        }
        catch (Exception ex)
        {
            CommandStatus = $"Copy failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExecuteCommandAsync()
    {
        var cmd = CommandInput?.Trim();
        if (string.IsNullOrWhiteSpace(cmd))
        {
            CommandStatus = "Enter a command";
            return;
        }

        CommandStatus = $"Executing: {cmd}";
        CommandInput = "";
        IsExecuting = true;

        try
        {
            await DispatchCommandAsync(cmd);
        }
        catch (Exception ex)
        {
            CommandStatus = $"Error: {ex.Message}";
        }
        finally
        {
            IsExecuting = false;
        }
    }

    private async Task RunSingleCommandAsync(string cmd)
    {
        await _data.ExecuteCommandAsync(cmd);
    }

    private async Task DispatchCommandAsync(string raw)
    {
        var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        switch (parts[0].ToLowerInvariant())
        {
            case "help":
                EmitHelp();
                break;
            case "providers":
                EmitProviders();
                break;
            case "fetch":
                await CmdFetchAsync(parts.Skip(1).ToArray());
                break;
            case "test":
                await CmdTestAsync(parts.Skip(1).ToArray());
                break;
            default:
                CommandStatus = $"Unknown command: {parts[0]}. Type 'help' for available commands.";
                break;
        }
    }

    private void EmitHelp()
    {
        AppendLine("Available commands:");
        AppendLine("  help              - Show this help");
        AppendLine("  providers         - List configured providers");
        AppendLine("  fetch <symbol>    - Fetch data for a symbol");
        AppendLine("  test <symbol>     - Test data for a symbol");
        AppendLine("");
        AppendLine("Type commands in the input box below.");
        Flush();
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
        if (parts.Length == 0)
        {
            CommandStatus = "Usage: fetch <symbol> [timeframe]";
            return;
        }

        var symbol = parts[0];
        var timeframe = parts.Length > 1 ? parts[1] : "1D";

        AppendLine($"Fetching {symbol} ({timeframe})...");
        Flush();

        try
        {
            await RunSingleCommandAsync($"fetch {symbol} {timeframe}");
            CommandStatus = $"Fetch completed for {symbol}";
        }
        catch (Exception ex)
        {
            CommandStatus = $"Fetch failed: {ex.Message}";
        }
    }

    private async Task CmdTestAsync(string[] parts)
    {
        if (parts.Length == 0)
        {
            CommandStatus = "Usage: test <symbol> [timeframe]";
            return;
        }

        var symbol = parts[0];
        var timeframe = parts.Length > 1 ? parts[1] : "1D";

        AppendLine($"Testing {symbol} ({timeframe})...");
        Flush();

        try
        {
            await RunSingleCommandAsync($"test {symbol} {timeframe}");
            CommandStatus = $"Test completed for {symbol}";
        }
        catch (Exception ex)
        {
            CommandStatus = $"Test failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LogManager.Logged -= OnLogged;
        _trailingFlush?.Dispose();
        _trailingFlush = null;
    }
}
