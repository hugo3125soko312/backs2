using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
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
public sealed record AutomationCalibration(AutomationRegion? Region, AutomationPoint? ClickPoint);

public sealed record AutomationParameters(
    string Symbol,
    string Timeframe,
    int StepCount,
    int DelayMs,
    Rectangle Region,
    int ClickX,
    int ClickY);

public sealed record CalibrationSnapshot(Bitmap Image, Rectangle WindowRect);

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

        var targetPid = (uint)processes[0].Id;
        IntPtr foundHwnd = IntPtr.Zero;

        EnumWindows((hWnd, lParam) =>
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == targetPid && IsWindowVisible(hWnd))
            {
                foundHwnd = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);

        return foundHwnd;
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
            if (!File.Exists(_path)) return new AutomationCalibration(null, null);
            try
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<AutomationCalibration>(json)
                       ?? new AutomationCalibration(null, null);
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.Data, "Failed to load calibration", ex);
                return new AutomationCalibration(null, null);
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
    private const int JumpToLatestPresses = 200;
    private const int PostJumpDelayMs = 600;

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

    public async Task RunAsync(AutomationParameters parameters, IProgress<string> progress)
    {
        var hwnd = _detector.FindWindow();
        if (hwnd == IntPtr.Zero)
        {
            progress.Report("Error: TradingView window not found.");
            return;
        }

        progress.Report("Focusing and maximizing TradingView...");
        _input.FocusAndMaximize(hwnd);
        await Task.Delay(500);

        var winRect = _detector.GetWindowRect(hwnd);
        int absClickX = winRect.Left + parameters.ClickX;
        int absClickY = winRect.Top  + parameters.ClickY;

        progress.Report("Focus click...");
        _input.ClickAt(absClickX, absClickY);
        await Task.Delay(300);

        progress.Report($"Jumping to latest bar ({JumpToLatestPresses} presses)...");
        for (int k = 0; k < JumpToLatestPresses; k++)
            _input.PressRightArrow();
        await Task.Delay(PostJumpDelayMs);

        progress.Report("Initializing capture...");
        _duplicator.Initialize();

        for (int i = 0; i < parameters.StepCount; i++)
        {
            progress.Report($"Step {i + 1}/{parameters.StepCount} — capturing...");

            using var frame = _duplicator.CaptureFrame(hwnd);
            if (frame == null)
            {
                progress.Report($"Warning: Failed to capture frame at step {i + 1}.");
                await Task.Delay(parameters.DelayMs);
                continue;
            }

            SaveRegion(frame, parameters.Region, parameters.Symbol, parameters.Timeframe, i);

            progress.Report("Navigating back (Left Arrow)...");
            _input.PressLeftArrow();
            await Task.Delay(parameters.DelayMs);
        }

        _duplicator.Dispose();
        progress.Report("Automation complete.");
    }

    private static void SaveRegion(Bitmap frame, Rectangle region, string symbol, string timeframe, int index)
    {
        var crop = region;
        crop.Intersect(new Rectangle(0, 0, frame.Width, frame.Height));
        if (crop.Width <= 0 || crop.Height <= 0) return;

        using var cropped = frame.Clone(crop, frame.PixelFormat);
        var dir = Path.Combine("data", "shots");
        Directory.CreateDirectory(dir);

        var path = Path.Combine(dir, $"{symbol}_{timeframe}_{index:D4}.png");
        cropped.Save(path, ImageFormat.Png);
    }
}

#endregion