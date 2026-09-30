using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;

[assembly: SupportedOSPlatform("windows")]

namespace backs2.Core.DataAutomation;

#region Records

public sealed record AutomationRegion(int X, int Y, int Width, int Height);
public sealed record AutomationPoint(int X, int Y);

public sealed record AutomationCalibration(
    Dictionary<string, AutomationRegion> ValueRegions,
    AutomationPoint? ClickPoint);

public sealed record AutomationParameters(
    string Symbol,
    string Timeframe,
    int StepCount,
    int DelayMs,
    Dictionary<string, Rectangle> ValueRegions,
    int ClickX,
    int ClickY);

public sealed record CalibrationSnapshot(Bitmap Image, Rectangle WindowRect);

public sealed record ExtractionProgress(
    string Phase,
    int Step,
    int Total,
    TimeSpan Elapsed,
    TimeSpan Eta,
    double PerStepMs,
    int CurrentDelayMs);

public enum CalibrationMode { Region, Point }

#endregion

#region Window detection

public sealed class TradingViewWindowDetector
{
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    public IntPtr FindWindow()
    {
        var processes = Process.GetProcessesByName("TradingView");
        if (processes.Length == 0)
        {
            LogManager.Warn(LogTag.Data, "TradingView process not found.");
            return IntPtr.Zero;
        }

        // Enumerate every candidate PID and return the first visible window
        // with a sane rect. GetProcess().MainWindowHandle is unreliable for
        // Chromium apps — it commonly returns 0 even when a real window exists.
        foreach (var proc in processes)
        {
            var targetPid = (uint)proc.Id;
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != targetPid) return true;
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowRect(hWnd, out RECT r);
                int w = r.Right - r.Left;
                int h = r.Bottom - r.Top;
                if (w < 400 || h < 300) return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero) return found;
        }

        return IntPtr.Zero;
    }

    public Rectangle GetWindowRect(IntPtr hWnd)
    {
        GetWindowRect(hWnd, out RECT r);
        return new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }
}

#endregion

#region Input simulation

public sealed class InputSimulatorService
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    private const int SW_MAXIMIZE = 3;

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    private const ushort VK_LEFT = 0x25;
    private const ushort VK_RIGHT = 0x27;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    public void FocusAndMaximize(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return;
        ShowWindow(hWnd, SW_MAXIMIZE);
        SetForegroundWindow(hWnd);
        Thread.Sleep(200);
        LogManager.Debug(LogTag.Data, "TradingView window focused and maximized.");
    }

    public void PressLeftArrow()  => PressExtendedKey(VK_LEFT);
    public void PressRightArrow() => PressExtendedKey(VK_RIGHT);

    private void PressExtendedKey(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].U.ki = new KEYBDINPUT
        { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_EXTENDEDKEY, time = 0, dwExtraInfo = IntPtr.Zero };
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].U.ki = new KEYBDINPUT
        { wVk = vk, wScan = 0, dwFlags = KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, time = 0, dwExtraInfo = IntPtr.Zero };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(20);
        LeftClick();
    }

    public void LeftClick()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].U.mi = new MOUSEINPUT
        { dx = 0, dy = 0, mouseData = 0, dwFlags = MOUSEEVENTF_LEFTDOWN, time = 0, dwExtraInfo = IntPtr.Zero };
        inputs[1].type = INPUT_MOUSE;
        inputs[1].U.mi = new MOUSEINPUT
        { dx = 0, dy = 0, mouseData = 0, dwFlags = MOUSEEVENTF_LEFTUP, time = 0, dwExtraInfo = IntPtr.Zero };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}

#endregion

#region Screen capture (PrintWindow)

public sealed class DesktopDuplicator : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private const uint PW_RENDERFULLCONTENT = 0x00000002;

    public void Initialize()
    {
        LogManager.Debug(LogTag.Data, "Capture initialized (PrintWindow mode).");
    }

    public Bitmap? CaptureFrame(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !IsWindow(hWnd)) return null;
        if (!GetWindowRect(hWnd, out var rect)) return null;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        IntPtr hdc = g.GetHdc();
        try
        {
            bool ok = PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT) || PrintWindow(hWnd, hdc, 0);
            if (!ok)
            {
                LogManager.Warn(LogTag.Data, "PrintWindow returned false.");
                bmp.Dispose();
                return null;
            }
        }
        finally { g.ReleaseHdc(hdc); }

        return bmp;
    }

    public void Dispose() { }
}

#endregion

#region Calibration persistence

public sealed class AutomationCalibrationService
{
    private readonly string _path;
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public AutomationCalibrationService(ConfigManager cfg)
    {
        Directory.CreateDirectory(cfg.ConfigDirectory);
        _path = Path.Combine(cfg.ConfigDirectory, "data_automation.json");
    }

    public AutomationCalibration Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_path))
                return new AutomationCalibration(new Dictionary<string, AutomationRegion>(), null);
            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AutomationCalibration>(json)
                       ?? new AutomationCalibration(new Dictionary<string, AutomationRegion>(), null);
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.Data, "Failed to load calibration", ex);
                return new AutomationCalibration(new Dictionary<string, AutomationRegion>(), null);
            }
        }
    }

    public void Save(AutomationCalibration cal)
    {
        lock (_gate)
        {
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(cal, _opts));
                LogManager.Info(LogTag.Data, "Calibration saved.");
            }
            catch (Exception ex) { LogManager.Error(LogTag.Data, "Failed to save calibration", ex); }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception ex) { LogManager.Error(LogTag.Data, "Failed to clear calibration", ex); }
        }
    }
}

#endregion

#region Orchestrator

public sealed class DataAutomationService
{
    private const int MaxAlignAttempts = 30;
    private const int AlignmentStepDelayMs = 80;

    private const int InitialDelayMs   = 1000;
    private const int AdaptiveMinMs    = 30;
    private const int AdaptiveMaxMs    = 2000;
    private const int PollIntervalMs   = 10;
    private const int HardTimeoutMs    = 5000;

    private readonly TradingViewWindowDetector _detector;
    private readonly InputSimulatorService _input;
    private readonly DesktopDuplicator _duplicator;
    private readonly AutomationCalibrationService _calibration;

    public DataAutomationService(ConfigManager cfg)
    {
        _detector = new TradingViewWindowDetector();
        _input = new InputSimulatorService();
        _duplicator = new DesktopDuplicator();
        _calibration = new AutomationCalibrationService(cfg);
    }

    public AutomationCalibration LoadCalibration() => _calibration.Load();
    public void SaveCalibration(AutomationCalibration cal) => _calibration.Save(cal);
    public void ClearCalibration() => _calibration.Clear();

    public async Task<CalibrationSnapshot?> PrepareCalibrationAsync()
    {
        var hwnd = _detector.FindWindow();
        if (hwnd == IntPtr.Zero) return null;

        _input.FocusAndMaximize(hwnd);
        await Task.Delay(500);

        _duplicator.Initialize();
        var frame = _duplicator.CaptureFrame(hwnd);
        _duplicator.Dispose();
        if (frame == null) return null;

        var rect = _detector.GetWindowRect(hwnd);
        return new CalibrationSnapshot(frame, rect);
    }

    /// <summary>
    /// Full extraction: walks every step from 0 (newest) to StepCount-1 (oldest).
    /// </summary>
    public Task RunAsync(
        AutomationParameters parameters,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct)
        => RunAsync(parameters, null, progress, ct);

    /// <summary>
    /// Filtered extraction: walks from step 0 to max(stepFilter), saving
    /// only the shots whose index is in the set. Used to re-capture the
    /// crops that failed OCR on a previous run.
    /// </summary>
    public async Task RunAsync(
        AutomationParameters parameters,
        HashSet<int>? stepFilter,
        IProgress<ExtractionProgress> progress,
        CancellationToken ct)
    {
        if (stepFilter is { Count: 0 }) return;

        int maxStep = stepFilter is { Count: > 0 }
            ? stepFilter.Max()
            : parameters.StepCount - 1;

        if (maxStep < 0) return;

        int totalTargets = stepFilter?.Count ?? parameters.StepCount;
        int captured = 0;

        var setupSw = Stopwatch.StartNew();

        void ReportSetup(string phase)
            => progress.Report(new ExtractionProgress(
                phase, 0, totalTargets, setupSw.Elapsed, TimeSpan.Zero, 0, InitialDelayMs));

        var hwnd = _detector.FindWindow();
        if (hwnd == IntPtr.Zero)
        {
            ReportSetup("Error: TradingView window not found");
            return;
        }

        ct.ThrowIfCancellationRequested();
        ReportSetup("Focusing TradingView");
        _input.FocusAndMaximize(hwnd);
        await Task.Delay(500, ct);

        ct.ThrowIfCancellationRequested();
        ReportSetup("Clicking anchor point");
        var winRect = _detector.GetWindowRect(hwnd);
        _input.ClickAt(winRect.Left + parameters.ClickX, winRect.Top + parameters.ClickY);
        await Task.Delay(300, ct);

        _duplicator.Initialize();
        try
        {
            ct.ThrowIfCancellationRequested();
            ReportSetup("Aligning to newest bar");

            string refKey = parameters.ValueRegions.ContainsKey("C") ? "C"
                          : parameters.ValueRegions.ContainsKey("O") ? "O"
                          : parameters.ValueRegions.Keys.First();
            var refRegion = parameters.ValueRegions[refKey];

            var hash0 = CaptureRegionHash(hwnd, refRegion);
            if (hash0 == null)
            {
                ReportSetup("Error: failed initial capture for alignment");
                return;
            }

            bool aligned = false;
            int arrowsUsed = 0;
            ulong currentHash = hash0.Value;

            for (; arrowsUsed < MaxAlignAttempts; arrowsUsed++)
            {
                ct.ThrowIfCancellationRequested();
                _input.PressLeftArrow();
                await Task.Delay(AlignmentStepDelayMs, ct);

                var h = CaptureRegionHash(hwnd, refRegion);
                if (h == null) continue;

                currentHash = h.Value;
                if (h.Value != hash0.Value) { aligned = true; break; }

                ReportSetup($"Aligning to newest bar (attempt {arrowsUsed + 1})");
            }

            if (!aligned)
            {
                ReportSetup(
                    $"Error: reference region did not change after {MaxAlignAttempts} left arrows. " +
                    "Confirm TradingView is in Replay mode and the anchor sits to the right of the newest candle.");
                return;
            }

            LogManager.Debug(LogTag.Data,
                $"Aligned after {arrowsUsed + 1} left arrow(s). Mode=" +
                (stepFilter == null ? "full" : "re-extract") +
                $", targets={(stepFilter?.Count ?? parameters.StepCount)}");

            var captureSw = Stopwatch.StartNew();
            var elapsedSw = Stopwatch.StartNew();

            for (int i = 0; i <= maxStep; i++)
            {
                ct.ThrowIfCancellationRequested();

                bool captureThis = stepFilter == null || stepFilter.Contains(i);

                ulong? capturedHash = null;

                if (captureThis)
                {
                    using var frame = _duplicator.CaptureFrame(hwnd);
                    if (frame != null)
                    {
                        SaveStep(frame, parameters.ValueRegions,
                                 parameters.Symbol, parameters.Timeframe, i);
                        capturedHash = HashRegionOf(frame, refRegion);
                    }
                }
                else
                {
                    capturedHash = CaptureRegionHash(hwnd, refRegion);
                }

                if (capturedHash != null) currentHash = capturedHash.Value;

                if (i < maxStep)
                {
                    _input.PressLeftArrow();

                    var latencySw = Stopwatch.StartNew();
                    ulong? newHash = null;
                    while (newHash == null)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(PollIntervalMs, ct);

                        var h = CaptureRegionHash(hwnd, refRegion);
                        if (h != null && h.Value != currentHash)
                            newHash = h;

                        if (latencySw.ElapsedMilliseconds > HardTimeoutMs)
                        {
                            LogManager.Warn(LogTag.Data,
                                $"Step {i + 1}: reference region did not change within {HardTimeoutMs} ms. " +
                                "Skipping wait; subsequent captures may be offset.");
                            break;
                        }
                    }

                    if (newHash != null) currentHash = newHash.Value;
                }

                if (captureThis)
                {
                    captured++;
                    double perStepSec = captureSw.Elapsed.TotalSeconds / captured;
                    TimeSpan eta = TimeSpan.FromSeconds(perStepSec * (totalTargets - captured));

                    progress.Report(new ExtractionProgress(
                        stepFilter == null ? "Capturing" : "Re-extracting",
                        captured, totalTargets, elapsedSw.Elapsed, eta,
                        perStepSec * 1000.0, 0));
                }
            }

            double finalPerStepMs = captured > 0
                ? captureSw.Elapsed.TotalSeconds / captured * 1000.0
                : 0;

            progress.Report(new ExtractionProgress(
                "Complete", captured, totalTargets, elapsedSw.Elapsed, TimeSpan.Zero,
                finalPerStepMs, 0));
        }
        finally
        {
            _duplicator.Dispose();
        }
    }

    private ulong? CaptureRegionHash(IntPtr hwnd, Rectangle region)
    {
        using var frame = _duplicator.CaptureFrame(hwnd);
        if (frame == null) return null;
        return HashRegionOf(frame, region);
    }

    private static ulong? HashRegionOf(Bitmap frame, Rectangle region)
    {
        var crop = region;
        crop.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        if (crop.Width <= 0 || crop.Height <= 0) return null;

        using var cropped = frame.Clone(crop, frame.PixelFormat);
        return HashBitmap(cropped);
    }

    private static ulong HashBitmap(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, bmp.PixelFormat);
        try
        {
            int bytes = Math.Abs(data.Stride) * data.Height;
            var buf = new byte[bytes];
            Marshal.Copy(data.Scan0, buf, 0, bytes);

            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < buf.Length; i++)
            {
                hash ^= buf[i];
                hash *= 1099511628211UL;
            }
            return hash;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void SaveStep(
        Bitmap frame,
        Dictionary<string, Rectangle> regions,
        string symbol,
        string timeframe,
        int index)
    {
        var dir = Path.Combine("data", "shots");
        Directory.CreateDirectory(dir);

        foreach (var (key, region) in regions)
        {
            var crop = region;
            crop.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
            if (crop.Width <= 0 || crop.Height <= 0) continue;

            using var cropped = frame.Clone(crop, frame.PixelFormat);
            var path = Path.Combine(dir, $"{symbol}_{timeframe}_{index:D4}_{key}.png");
            cropped.Save(path, ImageFormat.Png);
        }
    }
}

#endregion