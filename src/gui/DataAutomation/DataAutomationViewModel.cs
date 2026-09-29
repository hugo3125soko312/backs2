using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.DataAutomation;
using backs2.Core.Providers;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace backs2.ViewModels;

[SupportedOSPlatform("windows")]
public partial class DataAutomationViewModel : ObservableObject
{
    [ObservableProperty] private string _statusText = "Idle";
    [ObservableProperty] private int _stepCount = 100;
    [ObservableProperty] private int _delayMs = 250;
    [ObservableProperty] private string _symbol = "XAUUSD";
    [ObservableProperty] private string _timeframe = "1D";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAutomationCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAutomationCommand))]
    private bool _hasRegion;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartAutomationCommand))]
    private bool _hasClickPoint;

    [ObservableProperty] private string _regionDisplay = "Not calibrated";
    [ObservableProperty] private string _clickDisplay = "Not calibrated";

    public IReadOnlyList<string> Timeframes { get; } = TimeframeMap.All;

    public Rectangle? Region { get; private set; }
    public Point? ClickPoint { get; private set; }

    public event EventHandler? CalibrateRegionRequested;
    public event EventHandler? CalibrateClickRequested;

    private readonly DataAutomationService _service;

    public DataAutomationViewModel(DataAutomationService service)
    {
        _service = service;

        var saved = service.LoadCalibration();
        if (saved.Region != null)
            ApplyRegion(new Rectangle(saved.Region.X, saved.Region.Y,
                                     saved.Region.Width, saved.Region.Height), persist: false);
        if (saved.ClickPoint != null)
            ApplyClickPoint(new Point(saved.ClickPoint.X, saved.ClickPoint.Y), persist: false);

        if (saved.Region != null && saved.ClickPoint != null)
            StatusText = "Loaded saved calibration.";
    }

    [RelayCommand] private void CalibrateRegion() => CalibrateRegionRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void CalibrateClick()  => CalibrateClickRequested?.Invoke(this, EventArgs.Empty);

    public void ApplyRegion(Rectangle region, bool persist = true)
    {
        Region = region;
        RegionDisplay = $"X={region.X}  Y={region.Y}  W={region.Width}  H={region.Height}";
        HasRegion = true;
        if (persist) Persist();
    }

    public void ApplyClickPoint(Point p, bool persist = true)
    {
        ClickPoint = p;
        ClickDisplay = $"X={p.X}  Y={p.Y}";
        HasClickPoint = true;
        if (persist) Persist();
    }

    private void Persist()
    {
        var r = Region is { } rr ? new AutomationRegion(rr.X, rr.Y, rr.Width, rr.Height) : null;
        var c = ClickPoint is { } cp ? new AutomationPoint(cp.X, cp.Y) : null;
        _service.SaveCalibration(new AutomationCalibration(r, c));
    }

    [RelayCommand]
    private void ClearCalibration()
    {
        Region = null;
        ClickPoint = null;
        RegionDisplay = "Not calibrated";
        ClickDisplay = "Not calibrated";
        HasRegion = false;
        HasClickPoint = false;
        _service.ClearCalibration();
        StatusText = "Calibration cleared.";
    }

    private bool CanStartAutomation() => !IsRunning && HasRegion && HasClickPoint;

    [RelayCommand(CanExecute = nameof(CanStartAutomation))]
    private async Task StartAutomation()
    {
        if (Region == null || ClickPoint == null) return;

        IsRunning = true;
        var progress = new Progress<string>(s => StatusText = s);
        try
        {
            var parameters = new AutomationParameters(
                Symbol, Timeframe, StepCount, DelayMs,
                Region.Value, ClickPoint.Value.X, ClickPoint.Value.Y);

            await _service.RunAsync(parameters, progress);
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Data, "Automation failed", ex);
            StatusText = $"Error: {ex.Message}";
        }
        finally { IsRunning = false; }
    }
}