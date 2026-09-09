using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Net.Http;
using Serilog;
namespace backs2.Core;

public class LogManager
{
    public LogManager() { Log.Logger = new LoggerConfiguration().WriteTo.File("logs/runtime.log", rollingInterval: RollingInterval.Day).CreateLogger(); }
    public void Info(string message) => Log.Information(message); public void Error(string message) => Log.Error(message);      // Used by the Logs Tab UI to refresh the view     public List<string> GetLatestLogs()     {         return File.ReadAllLines("logs/runtime.log").Reverse().Take(100).ToList();     } }
}