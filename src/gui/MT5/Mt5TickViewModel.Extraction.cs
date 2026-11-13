using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.MT5;
using backs2.Core.Writers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Media;

namespace backs2.ViewModels;

public partial class Mt5TickViewModel
{
    // ── Fetch settings ──────────────────────────────────────────────
    [ObservableProperty] private string _symbol = "GOLD.pro";
    [ObservableProperty] private int    _lookbackHours = 24;
    [ObservableProperty] private int    _targetTicks = 5000;
    [ObservableProperty] private int    _chunkSize = 50000;
    [ObservableProperty] private bool   _populateGrid = true;

    // ── Display state ───────────────────────────────────────────────
    [ObservableProperty] private double _fetchProgress;
    [ObservableProperty] private string _fetchProgressText = "";
    [ObservableProperty] private string _fetchEtaText = "";
    [ObservableProperty] private string _fetchSpeedText = "";
    [ObservableProperty] private string _terminalInfoText = "";
    [ObservableProperty] private string _gridCapText = "";

    // ── Verify panel state ──────────────────────────────────────────
    [ObservableProperty] private bool   _isVerifyExpanded = false;
    [ObservableProperty] private int    _verifySpanValue = 30;
    [ObservableProperty] private string _verifySpanUnit = "Years";
    [ObservableProperty] private string _verifyAnchorDate = "2026/10/02 10:00:00:000";
    [ObservableProperty] private double _verifyUtcOffsetHours = 0.0;
    [ObservableProperty] private string _verifyResolvedText = "";
    [ObservableProperty] private string _verifyStatusText = "";
    [ObservableProperty] private string _verifyComparisonText = "";
    [ObservableProperty] private System.Windows.Media.Brush _verifyComparisonBrush = Brushes.Gray;

    public string[] SpanUnits { get; } = new[]
    {
        "Seconds", "Minutes", "Hours", "Days", "Weeks", "Months", "Years"
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadLastFetchRangeCommand))]
    private bool _hasLastFetch;

    private long _lastFetchFromMsc = 0;
    private long _lastFetchEndMsc  = 0;
    private int  _lastFetchCount   = 0;
    private int  _lastVerifyCount  = 0;

    partial void OnTargetTicksChanged(int value)
    {
        if (value > 500_000 && PopulateGrid)
        {
            PopulateGrid = false;
            GridCapText = $"Grid auto-disabled — target {value:N0} exceeds 500,000. CSV streaming continues.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task FetchTicks()
    {
        // Implementation continues in original file
    }

    private static async Task DrainAndCloseAsync(
        Channel<IReadOnlyList<Mt5Tick>>? channel,
        Task? flushTask,
        Mt5TickStreamWriter? writer)
    {
        // Implementation continues in original file
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task GetTerminalInfo()
    {
        // Implementation continues in original file
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task SaveCsv()
    {
        // Implementation continues in original file
    }

    partial void OnVerifySpanValueChanged(int value)          => UpdateResolvedPreview();
    partial void OnVerifySpanUnitChanged(string value)        => UpdateResolvedPreview();
    partial void OnVerifyAnchorDateChanged(string value)      => UpdateResolvedPreview();
    partial void OnVerifyUtcOffsetHoursChanged(double value)  => UpdateResolvedPreview();

    private bool CanLoadLastFetchRange() => HasLastFetch;

    [RelayCommand(CanExecute = nameof(CanLoadLastFetchRange))]
    private void LoadLastFetchRange()
    {
        // Implementation continues in original file
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private async Task VerifyRange()
    {
        // Implementation continues in original file
    }

    [RelayCommand(CanExecute = nameof(CanRunIdle))]
    private void ApplyToFetch()
    {
        // Implementation continues in original file
    }

    private static bool TryComputeRange(
        string anchorStr, int spanValue, string spanUnit, double utcOffsetHours,
        out long fromMsc, out long toMsc, out string err)
    {
        // Implementation continues in original file
        fromMsc = 0;
        toMsc = 0;
        err = "";
        return false;
    }

    private void UpdateResolvedPreview()
    {
        // Implementation continues in original file
    }

    private static bool TryParseTimestamp(string s, double utcOffsetHours, out long msc, out string err)
    {
        // Implementation continues in original file
        msc = 0;
        err = "";
        return false;
    }

    private static string FormatUtc(long msc)
        => DateTimeOffset.FromUnixTimeMilliseconds(msc)
           .UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " UTC";

    private static string FormatMscForOffset(long msc, double offsetHours)
    {
        var dto = DateTimeOffset.FromUnixTimeMilliseconds(msc);
        var shifted = dto.ToOffset(TimeSpan.FromHours(offsetHours));
        return shifted.ToString("yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture)
               + ":" + shifted.Millisecond.ToString("D3", CultureInfo.InvariantCulture);
    }

    private static string FormatDuration(long ms)
    {
        // Implementation continues in original file
        return "";
    }
}
