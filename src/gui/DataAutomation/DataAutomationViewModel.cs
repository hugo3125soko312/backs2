using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.DataAutomation;
using backs2.Core.Providers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.ViewModels;

[SupportedOSPlatform("windows")]
public partial class DataAutomationViewModel : ObservableObject
{
    // ── Extraction tab ───────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private int _stepCount = 100;
    [ObservableProperty] private int _delayMs = 250;
    [ObservableProperty] private string _symbol = "XAUUSD";
    [ObservableProperty] private string _timeframe = "1D";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAutomationCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopAutomationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReExtractCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopReExtractCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAutomationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReExtractCommand))]
    private bool _hasAllValueRegions;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAutomationCommand))]
    [NotifyCanExecuteChangedFor(nameof(ReExtractCommand))]
    private bool _hasClickPoint;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviewCalibrationCommand))]
    private bool _hasAnyCalibration;

    [ObservableProperty] private string _dateDisplay = "not set (optional)";
    [ObservableProperty] private string _timeDisplay = "not set (optional)";
    [ObservableProperty] private string _oDisplay = "not set";
    [ObservableProperty] private string _hDisplay = "not set";
    [ObservableProperty] private string _lDisplay = "not set";
    [ObservableProperty] private string _cDisplay = "not set";
    [ObservableProperty] private string _vDisplay = "not set";
    [ObservableProperty] private string _clickDisplay = "not set";
    [ObservableProperty] private double _extractionProgress;

    [ObservableProperty] private string _elapsedDisplay = "—";
    [ObservableProperty] private string _etaDisplay = "—";
    [ObservableProperty] private string _delayDisplay = "—";
    [ObservableProperty] private string _perStepDisplay = "—";

    [ObservableProperty] private string _reExtractInput = "";

    public IReadOnlyList<string> Timeframes { get; } = TimeframeMap.All;

    public static readonly string[] RequiredValueKeys = { "O", "H", "L", "C", "V" };

    private readonly Dictionary<string, Rectangle> _valueRegions = new();
    private Point? _clickPoint;

    public event EventHandler<string>? CalibrateValueRequested;
    public event EventHandler? CalibrateClickRequested;
    public event EventHandler? PreviewCalibrationRequested;

    // ── Parsing tab ──────────────────────────────────────────────────
    [ObservableProperty] private string _parseTicker = "XAUUSD";
    [ObservableProperty] private string _parseTimeframe = "1D";
    [ObservableProperty] private bool _useGpu;
    [ObservableProperty] private double _parseProgress;
    [ObservableProperty] private string _parseStatus = "Idle";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartParsingCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopParsingCommand))]
    private bool _isParsing;

    [ObservableProperty] private string _parseElapsedDisplay = "—";
    [ObservableProperty] private string _parseEtaDisplay = "—";
    [ObservableProperty] private string _parseRateDisplay = "—";

    // ── Analysis tab ─────────────────────────────────────────────────
    [ObservableProperty] private string _analysisPath = "data/parsed/XAUUSD_1h_4.json";
    [ObservableProperty] private int _analysisTimezoneOffset = 2;
    [ObservableProperty] private string _analysisSessionClosedHours = "0,1";
    [ObservableProperty] private bool _analysisSkipWeekend = true;
    [ObservableProperty] private string _analysisResultText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    private bool _isAnalyzing;

    private readonly DataAutomationService _service;
    private readonly OcrWorkerClient _parsing;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _parseCts;

    public DataAutomationViewModel(
        DataAutomationService service,
        OcrWorkerClient parsing)
    {
        _service = service;
        _parsing = parsing;

        var saved = service.LoadCalibration();

        if (saved.ValueRegions != null)
        {
            foreach (var (key, r) in saved.ValueRegions)
                ApplyValueRegion(key, new Rectangle(r.X, r.Y, r.Width, r.Height), persist: false);
        }

        if (saved.ClickPoint != null)
            ApplyClickPoint(new Point(saved.ClickPoint.X, saved.ClickPoint.Y), persist: false);

        RefreshCalibrationFlags();

        if (HasAllValueRegions && HasClickPoint)
            StatusText = "Loaded saved calibration.";
    }

    [RelayCommand]
    private void CalibrateValue(string key)
        => CalibrateValueRequested?.Invoke(this, key);

    [RelayCommand]
    private void CalibrateClick()
        => CalibrateClickRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(HasAnyCalibration))]
    private void PreviewCalibration()
        => PreviewCalibrationRequested?.Invoke(this, EventArgs.Empty);

    public void ApplyValueRegion(string key, Rectangle region, bool persist = true)
    {
        _valueRegions[key] = region;

        string display = $"X={region.X}  Y={region.Y}  W={region.Width}  H={region.Height}";
        switch (key)
        {
            case "DATE": DateDisplay = display; break;
            case "TIME": TimeDisplay = display; break;
            case "O":    ODisplay = display; break;
            case "H":    HDisplay = display; break;
            case "L":    LDisplay = display; break;
            case "C":    CDisplay = display; break;
            case "V":    VDisplay = display; break;
        }

        RefreshCalibrationFlags();
        if (persist) Persist();
    }

    public void ApplyClickPoint(Point p, bool persist = true)
    {
        _clickPoint = p;
        ClickDisplay = $"X={p.X}  Y={p.Y}";
        HasClickPoint = true;
        RefreshCalibrationFlags();
        if (persist) Persist();
    }

    public (Dictionary<string, Rectangle> Regions, Point? Anchor) GetCalibrationSnapshot()
        => (new Dictionary<string, Rectangle>(_valueRegions), _clickPoint);

    private void RefreshCalibrationFlags()
    {
        HasAllValueRegions = RequiredValueKeys.All(_valueRegions.ContainsKey);
        HasAnyCalibration = _valueRegions.Count > 0 || _clickPoint != null;
    }

    private void Persist()
    {
        var regions = _valueRegions.ToDictionary(
            kvp => kvp.Key,
            kvp => new AutomationRegion(kvp.Value.X, kvp.Value.Y, kvp.Value.Width, kvp.Value.Height));

        var c = _clickPoint is { } cp ? new AutomationPoint(cp.X, cp.Y) : null;
        _service.SaveCalibration(new AutomationCalibration(regions, c));
    }

    [RelayCommand]
    private void ClearCalibration()
    {
        _valueRegions.Clear();
        _clickPoint = null;
        DateDisplay = TimeDisplay = "not set (optional)";
        ODisplay = HDisplay = LDisplay = CDisplay = VDisplay = "not set";
        ClickDisplay = "not set";
        HasAllValueRegions = false;
        HasClickPoint = false;
        HasAnyCalibration = false;
        ElapsedDisplay = EtaDisplay = DelayDisplay = PerStepDisplay = "—";
        _service.ClearCalibration();
        StatusText = "Calibration cleared.";
    }

    private bool CanStartAutomation() => !IsRunning && HasAllValueRegions && HasClickPoint;
    private bool CanStopAutomation()  => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStartAutomation))]
    private async Task StartAutomation()
    {
        if (_clickPoint == null || _valueRegions.Count == 0) return;

        IsRunning = true;
        ExtractionProgress = 0;
        ElapsedDisplay = "0s";
        EtaDisplay = "—";
        DelayDisplay = "—";
        PerStepDisplay = "—";
        _cts = new CancellationTokenSource();

        var progress = new Progress<ExtractionProgress>(ExtractionProgress_Update);

        try
        {
            var parameters = new AutomationParameters(
                Symbol, Timeframe, StepCount, 0,
                new Dictionary<string, Rectangle>(_valueRegions),
                _clickPoint.Value.X, _clickPoint.Value.Y);

            await _service.RunAsync(parameters, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Stopped by user.";
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Data, "Automation failed", ex);
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopAutomation))]
    private void StopAutomation() => _cts?.Cancel();

    private bool CanStopReExtract() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanStartAutomation))]
    private async Task ReExtract()
    {
        if (_clickPoint == null || _valueRegions.Count == 0) return;

        var stepSet = ParseStepIndices(ReExtractInput);
        if (stepSet == null || stepSet.Count == 0)
        {
            StatusText = "No valid step indices provided. Paste the contents of data/parsed/{symbol}_{timeframe}_misses.txt.";
            return;
        }

        IsRunning = true;
        ExtractionProgress = 0;
        ElapsedDisplay = "0s";
        EtaDisplay = "—";
        DelayDisplay = "—";
        PerStepDisplay = "—";
        _cts = new CancellationTokenSource();

        var progress = new Progress<ExtractionProgress>(ExtractionProgress_Update);

        try
        {
            var parameters = new AutomationParameters(
                Symbol, Timeframe, StepCount, 0,
                new Dictionary<string, Rectangle>(_valueRegions),
                _clickPoint.Value.X, _clickPoint.Value.Y);

            await _service.RunAsync(parameters, stepSet, progress, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Re-extraction stopped by user.";
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Data, "Re-extraction failed", ex);
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsRunning = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopReExtract))]
    private void StopReExtract() => _cts?.Cancel();

    private static HashSet<int>? ParseStepIndices(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var set = new HashSet<int>();
        foreach (var token in input.Split(
            new[] { ',', ';', '\n', '\r', ' ', '\t' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), out int n) && n >= 0)
                set.Add(n);
        }

        return set.Count > 0 ? set : null;
    }

    private void ExtractionProgress_Update(ExtractionProgress p)
    {
        if (p.Total > 0 && p.Step > 0)
            ExtractionProgress = (double)p.Step / p.Total * 100.0;

        var elapsed = FormatDuration(p.Elapsed);
        ElapsedDisplay = elapsed;

        if (p.Step > 0 && p.Total > 0)
        {
            string perStep = p.PerStepMs > 0 ? $"{p.PerStepMs:F0} ms" : "—";
            string delay   = p.CurrentDelayMs > 0 ? $"{p.CurrentDelayMs} ms" : "—";

            PerStepDisplay = perStep;
            DelayDisplay = delay;

            if (p.Step < p.Total)
            {
                EtaDisplay = FormatDuration(p.Eta);
                StatusText = $"{p.Phase} {p.Step}/{p.Total}";
            }
            else
            {
                EtaDisplay = "done";
                StatusText = $"{p.Phase} {p.Step}/{p.Total} — total {elapsed}";
            }
        }
        else
        {
            EtaDisplay = "—";
            DelayDisplay = "—";
            PerStepDisplay = "—";
            StatusText = p.Phase;
        }
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "0s";
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:F0}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalHours}h {t.Minutes}m";
    }

    // ── Parsing commands ─────────────────────────────────────────────
    private bool CanStartParsing() => !IsParsing;
    private bool CanStopParsing()  => IsParsing;

    [RelayCommand(CanExecute = nameof(CanStartParsing))]
    private async Task StartParsing()
    {
        IsParsing = true;
        ParseProgress = 0;
        ParseStatus = "Starting OCR worker...";
        ParseElapsedDisplay = "0s";
        ParseEtaDisplay = "—";
        ParseRateDisplay = "—";

        _parseCts = new CancellationTokenSource();
        var sw = Stopwatch.StartNew();

        var progress = new Progress<(int Current, int Total)>(p =>
        {
            ParseProgress = p.Total > 0 ? (double)p.Current / p.Total * 100.0 : 0;

            var elapsed = sw.Elapsed;
            ParseElapsedDisplay = FormatDuration(elapsed);

            if (p.Current > 0 && p.Current < p.Total)
            {
                double perStepSec = elapsed.TotalSeconds / p.Current;
                var eta = TimeSpan.FromSeconds(perStepSec * (p.Total - p.Current));
                ParseEtaDisplay = FormatDuration(eta);
                ParseRateDisplay = perStepSec > 0 ? $"{1.0 / perStepSec:F1}/s" : "—";
            }
            else if (p.Current >= p.Total && p.Total > 0)
            {
                ParseEtaDisplay = "done";
                if (elapsed.TotalSeconds > 0)
                    ParseRateDisplay = $"{p.Total / elapsed.TotalSeconds:F1}/s";
            }

            ParseStatus = $"Parsing {p.Current}/{p.Total}...";
        });

        try
        {
            var result = await _parsing.ParseAsync(
                new ParseRequest(ParseTicker, ParseTimeframe, UseGpu),
                progress,
                _parseCts.Token);

            ParseStatus =
                $"Done — {result.ParsedShots}/{result.TotalShots} parsed, " +
                $"{result.FailedShots} failed → {result.OutputPath}";
        }
        catch (OperationCanceledException)
        {
            ParseStatus = "Parsing stopped by user.";
            LogManager.Info(LogTag.Data, "Parsing stopped by user.");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Data, "Parsing failed", ex);
            ParseStatus = $"Error: {ex.Message}";
        }
        finally
        {
            _parseCts.Dispose();
            _parseCts = null;
            IsParsing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStopParsing))]
    private void StopParsing() => _parseCts?.Cancel();

    // ── Analysis commands ────────────────────────────────────────────
    private bool CanAnalyze() => !IsAnalyzing;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task Analyze()
    {
        IsAnalyzing = true;
        AnalysisResultText = "Analyzing...";
        try
        {
            var hours = ParseHours(AnalysisSessionClosedHours);
            var service = new AnalysisService();
            var result = await service.AnalyzeAsync(
                AnalysisPath,
                AnalysisTimezoneOffset,
                hours,
                AnalysisSkipWeekend,
                CancellationToken.None);
            AnalysisResultText = result;
        }
        catch (Exception ex)
        {
            AnalysisResultText = $"Error: {ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private static HashSet<int> ParseHours(string input)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(input)) return set;
        foreach (var token in input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), out int h) && h >= 0 && h <= 23)
                set.Add(h);
        }
        return set;
    }
}