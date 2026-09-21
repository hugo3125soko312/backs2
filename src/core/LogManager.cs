using System.Collections.Generic;
using System.IO;
using System.Linq;
using Serilog;

namespace backs2.Core;

public class LogManager
{
    public LogManager()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File("logs/runtime.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();
    }

    public void Info(string message) => Log.Information(message);

    public void Error(string message) => Log.Error(message);

    // Used by the Logs Tab UI to refresh the view
    public List<string> GetLatestLogs()
    {
        var path = "logs/runtime.log";
        if (!File.Exists(path))
            return new List<string>();

        return File.ReadAllLines(path)
            .Reverse()
            .Take(100)
            .ToList();
    }
}