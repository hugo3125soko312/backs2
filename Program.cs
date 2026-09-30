using System;
using Avalonia;
using backs2.Core;
using backs2.Core.DataAutomation;

namespace backs2;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Worker mode: run OCR in isolation and exit before Avalonia loads.
        if (args.Length > 0 && args[0] == "--ocr-worker")
        {
            Environment.ExitCode = OcrWorkerHost.RunAsync().GetAwaiter().GetResult();
            return;
        }

        try
        {
            LogManager.Initialize();
            LogManager.Info(LogTag.App, "Process start");
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.App, "Fatal exception", ex);
            LogManager.Shutdown();
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}