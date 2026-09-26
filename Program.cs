using System;
using Avalonia;
using backs2.Core;

namespace backs2;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LogManager.Initialize("Info");
        LogManager.Info(LogTag.App, "Process start");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            LogManager.Info(LogTag.App, "Process exit (clean)");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.App, "Unhandled exception at process level", ex);
            throw;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}