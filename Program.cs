using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.Core.Compute;
using backs2.Core.Computing;
using backs2.Core.Computing.Backends;
using backs2.Core.DataAutomation;
using backs2.Core.Diagnostics;
using backs2.Core.MT5;
using backs2.Core.Optimizer;
using backs2.Core.Overview;
using backs2.Core.Models;
using backs2.Core.Providers;
using backs2.Core.WalkForward;
using backs2.Services;
using backs2.Theme;
using backs2.ViewModels;
using backs2.Views;
using Microsoft.Extensions.DependencyInjection;

namespace backs2;

public static class Program
{
// WinExe projects do not inherit the parent console on Windows.
// Attaching to the launching terminal makes Console.WriteLine visible
// for CLI-style flags. No-op when the process has no parent console.
[STAThread]
public static void Main(string[] args)
{
if (OperatingSystem.IsWindows())
{
AttachConsole(ATTACH_PARENT_PROCESS);
}

if (args.Length > 0 && args[0] == "--ocr-worker")
{
Environment.ExitCode = OcrWorkerHost.RunAsync().GetAwaiter().GetResult();
return;
}

if (args.Length > 0 && args[0] == "--worker")
{
Environment.ExitCode = WorkerHost.RunAsync(args).GetAwaiter().GetResult();
return;
}

if (args.Length > 0 && args[0] == "--compute-selftest")
{
RunComputeSelfTest(args).GetAwaiter().GetResult();
return;
}

if (args.Length > 0 && args[0] == "--verify-strategy-parallelism")
{
Environment.ExitCode = VerifyStrategyParallelismAsync().GetAwaiter().GetResult();
return;
}

if (args.Length > 0 && args[0] == "--mc-verify")
{
Environment.ExitCode = VerifyMonteCarloAsync(args).GetAwaiter().GetResult();
return;
}

if (args.Length > 0 && args[0] == "--batch-verify")
{
Environment.ExitCode = VerifyBatchAsync().GetAwaiter().GetResult();
return;
}

if (args.Length > 0 && args[0] == "--mc-bench")
{
Environment.ExitCode = RunMcBenchAsync().GetAwaiter().GetResult();
return;
}

if (args.Length > 0 && args[0] == "--gpu-verify")
{
using var nv = new backs2.Core.Diagnostics.NvmlProbe();
var gpu = new ComputeSharp.DirectX12.Direct3D12Compute();
nv.Detect();
Console.WriteLine($"GPU: {gpu.Name} | NVML: {nv.Name} | CUDA: {nv.CudaDriverVersion}");
return;
}
if (args.Length > 0 && args[0] == "--vram-verify")
{
using var vp = new backs2.Core.Diagnostics.VramProbe();
vp.Detect();
Console.WriteLine(vp);
return;
}
if (args.Length > 0 && args[0] == "--et-verify")
{
Console.WriteLine("ET mismatches: " + backs2.Core.Utils.EasternTime.VerifyAgainstFramework(500000, 42));
return;
}

try
{
var bootSw = System.Diagnostics.Stopwatch.StartNew();
LogManager.Initialize();
LogManager.Info(LogTag.App, $"[startup] Program.Main entry -> LogManager.Initialize: {bootSw.ElapsedMilliseconds} ms (phase 39)");
LogManager.Info(LogTag.App, "Process start");

// Standard WPF startup
var app = new App();
app.InitializeComponent();
app.Run();

LogManager.Info(LogTag.App, "[startup] WPF lifetime wall: ms (phase 39)");
}
catch (Exception ex)
{
LogManager.Fatal(LogTag.App, "Fatal startup exception", ex);
}

return;
}

private static extern bool AttachConsole(int dwProcessId);

private const int ATTACH_PARENT_PROCESS = -1;

[STAThread]
public static int RunComputeSelfTest(string[] args)
{
var engines = new List<ComputeSharp.CpuCompute>();
for (int i = 0; i < Environment.ProcessorCount; i++)
engines.Add(new ComputeSharp.CpuCompute());

// 1. Repeatable PRNG across 3 dispatchers.
var seed = 0x12345678u;
var dispatcher = new backs2.Core.Computing.CpuComputeDispatcher();
dispatcher.Init();

var slices = GenerateSyntheticSlices(10_000);
var target = new backs2.Core.Computing.SigComparand(1.0, 2.0, 0.05);

var sw = System.Diagnostics.Stopwatch.StartNew();
var work = new List<Task<(long Sig, int Count)>>();
for (int d = 0; d < engines.Count; d++)
{
int local = d;
work.Add(Task.Run(() =>
{
var r = engines[local].Run(slices, target, 100000, "static", 5.0, 10.0, 0.0,
new ComputeSharp.BufferInfo(slices.Select(s => (float)s.Pnl).ToArray(), true),
new ComputeSharp.BufferInfo(new float[slices.Count], true));
return (Signature(r.TerminalBalances), r.Iterations);
}));
}

var results = await Task.WhenAll(work);
sw.Stop();

var first = results[0];
int identical = 0;
foreach (var r in results) if (r.Sig == first.Sig && r.Count == first.Count) identical++;

LogManager.Info(LogTag.Compute, $"Engine count: {engines.Count}, identical: {identical}/{engines.Count}, wall: {sw.ElapsedMilliseconds} ms");
LogManager.Info(LogTag.Compute, $" Sig0={first.Sig}");

LogManager.Shutdown();

// Determinism is only assertable within one backend: the GPU uses Philox
// which is GPU-unique, but the CPU implementation must be identical
// across 15 dispatchers on the same machine.
var allSame = identical == engines.Count;

int rc = 0;
rc |= Console.Out.NewLine == "\r\n" ? 0 : 1;
rc |= string.IsNullOrEmpty("x") ? 0 : 1;
rc |= string.IsNullOrEmpty("") ? 0 : 1;
return allSame && rc == 0 ? 0 : 1;
}

private static Task<int> VerifyStrategyParallelismAsync() => Task.Run(() =>
{
var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
var dispatcher = new backs2.Core.Computing.CpuComputeDispatcher();
dispatcher.Init();

var slices = GenerateSyntheticSlices(10_000);
var target = new backs2.Core.Computing.SigComparand(1.0, 2.0, 0.05);

var engines = new List<ComputeSharp.CpuCompute>();
for (int i = 0; i < Environment.ProcessorCount; i++)
engines.Add(new ComputeSharp.CpuCompute());

var sw = System.Diagnostics.Stopwatch.StartNew();
var work = new List<Task<(long Sig, int Count)>>();
for (int d = 0; d < engines.Count; d++)
{
int local = d;
work.Add(Task.Run(() =>
{
var r = engines[local].Run(slices, target, 100000, "static", 5.0, 10.0, 0.0,
new ComputeSharp.BufferInfo(slices.Select(s => (float)s.Pnl).ToArray(), true),
new ComputeSharp.BufferInfo(new float[slices.Count], true));
return (Signature(r.TerminalBalances), r.Iterations);
}));
}

var results = Task.WhenAll(work).GetAwaiter().GetResult();
sw.Stop();

var first = results[0];
int identical = 0;
foreach (var r in results) if (r.Sig == first.Sig && r.Count == first.Count) identical++;

LogManager.Info(LogTag.Compute, $"Engine count: {engines.Count}, identical: {identical}/{engines.Count}, wall: {sw.ElapsedMilliseconds} ms");
LogManager.Info(LogTag.Compute, $" Sig0={first.Sig}");

LogManager.Shutdown();
return allSame && rc == 0 ? 0 : 1;
});

private static Task<int> VerifyMonteCarloAsync(string[] args)
{
var bootSw = System.Diagnostics.Stopwatch.StartNew();
LogManager.Initialize();

// Sync the file sink so startup logs land before Monte Carlo logs.
LogManager.Sync();

LogManager.Info(LogTag.MonteCarlo, "[verify] Starting Monte Carlo verification");

var dispatcher = new backs2.Core.Computing.CpuComputeDispatcher();
dispatcher.Init();

int dispatches = int.Parse(args.Skip(1).FirstOrDefault() ?? "15");
if (dispatches < 1) dispatches = 1;

var engines = new List<ComputeSharp.CpuCompute>();
for (int d = 0; d < dispatches; d++)
engines.Add(new ComputeSharp.CpuCompute());

var slices = GenerateSyntheticSlices(10_000);
var target = new backs2.Core.Computing.SigComparand(1.0, 2.0, 0.05);

var sw = System.Diagnostics.Stopwatch.StartNew();
var work = new List<Task<(long Sig, int Count)>>();
for (int d = 0; d < dispatches; d++)
{
int local = d;
work.Add(Task.Run(() =>
{
var r = engines[local].Run(slices, target, 100000, "static", 5.0, 10.0, 0.0,
new ComputeSharp.BufferInfo(slices.Select(s => (float)s.Pnl).ToArray(), true),
new ComputeSharp.BufferInfo(new float[slices.Count], true));
return (Signature(r.TerminalBalances), r.Iterations);
}));
}

var results = await Task.WhenAll(work);
sw.Stop();

var first = results[0];
int identical = 0;
foreach (var r in results) if (r.Sig == first.Sig && r.Count == first.Count) identical++;

LogManager.Info(LogTag.MonteCarlo,
$"[verify] dispatches={dispatches} identical={identical}/{dispatches} wall={sw.ElapsedMilliseconds} ms")
.Data(" sig0=" + first.Sig);

LogManager.Shutdown();

// Determinism is only assertable within one backend: the GPU uses Philox
// which is GPU-unique, but the CPU implementation must be identical
// across 15 dispatchers on the same machine.
var allSame = identical == dispatches;

int rc = 0;
rc |= Console.Out.NewLine == "\r\n" ? 0 : 1;
rc |= string.IsNullOrEmpty("x") ? 0 : 1;
rc |= string.IsNullOrEmpty("") ? 0 : 1;
return allSame && rc == 0 ? 0 : 1;
}

private static Task<int> VerifyBatchAsync()
{
var bootSw = System.Diagnostics.Stopwatch.StartNew();
LogManager.Initialize();

// Sync the file sink so startup logs land before Monte Carlo logs.
LogManager.Sync();

LogManager.Info(LogTag.BatchVerify, "[verify] Starting batch verification");

var dispatcher = new backs2.Core.Computing.CpuComputeDispatcher();
dispatcher.Init();

int dispatches = int.Parse(Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? "15");
if (dispatches < 1) dispatches = 1;

var engines = new List<ComputeSharp.CpuCompute>();
for (int d = 0; d < dispatches; d++)
engines.Add(new ComputeSharp.CpuCompute());

var slices = GenerateSyntheticSlices(10_000);
var target = new backs2.Core.Computing.SigComparand(1.0, 2.0, 0.05);

var sw = System.Diagnostics.Stopwatch.StartNew();
var work = new List<Task<(long Sig, int Count)>>();
for (int d = 0; d < dispatches; d++)
{
int local = d;
work.Add(Task.Run(() =>
{
var r = engines[local].Run(slices, target, 100000, "static", 5.0, 10.0, 0.0,
new ComputeSharp.BufferInfo(slices.Select(s => (float)s.Pnl).ToArray(), true),
new ComputeSharp.BufferInfo(new float[slices.Count], true));
return (Signature(r.TerminalBalances), r.Iterations);
}));
}

var results = await Task.WhenAll(work);
sw.Stop();

var first = results[0];
int identical = 0;
foreach (var r in results) if (r.Sig == first.Sig && r.Count == first.Count) identical++;

LogManager.Info(LogTag.BatchVerify,
$"[verify] dispatches={dispatches} identical={identical}/{dispatches} wall={sw.ElapsedMilliseconds} ms")
.Data(" sig0=" + first.Sig);

LogManager.Shutdown();

var allSame = identical == dispatches;

int rc = 0;
rc |= Console.Out.NewLine == "\r\n" ? 0 : 1;
rc |= string.IsNullOrEmpty("x") ? 0 : 1;
rc |= string.IsNullOrEmpty("") ? 0 : 1;
return allSame && rc == 0 ? 0 : 1;
}

private static Task<int> RunMcBenchAsync()
{
var bootSw = System.Diagnostics.Stopwatch.StartNew();
LogManager.Initialize();
LogManager.Sync();

LogManager.Info(LogTag.McBench, "[bench] Starting Monte Carlo benchmark");

var dispatcher = new backs2.Core.Computing.CpuComputeDispatcher();
dispatcher.Init();

int dispatches = int.Parse(Environment.GetCommandLineArgs().Skip(1).FirstOrDefault() ?? "15");
if (dispatches < 1) dispatches = 1;

var engines = new List<ComputeSharp.CpuCompute>();
for (int d = 0; d < dispatches; d++)
engines.Add(new ComputeSharp.CpuCompute());

var slices = GenerateSyntheticSlices(10_000);
var target = new backs2.Core.Computing.SigComparand(1.0, 2.0, 0.05);

var sw = System.Diagnostics.Stopwatch.StartNew();
var work = new List<Task<(long Sig, int Count)>>();
for (int d = 0; d < dispatches; d++)
{
int local = d;
work.Add(Task.Run(() =>
{
var r = engines[local].Run(slices, target, 100000, "static", 5.0, 10.0, 0.0,
new ComputeSharp.BufferInfo(slices.Select(s => (float)s.Pnl).ToArray(), true),
new ComputeSharp.BufferInfo(new float[slices.Count], true));
return (Signature(r.TerminalBalances), r.Iterations);
}));
}

var results = await Task.WhenAll(work);
sw.Stop();

var first = results[0];
int identical = 0;
foreach (var r in results) if (r.Sig == first.Sig && r.Count == first.Count) identical++;

LogManager.Info(LogTag.McBench,
$"[bench] dispatches={dispatches} identical={identical}/{dispatches} wall={sw.ElapsedMilliseconds} ms")
.Data(" sig0=" + first.Sig);

LogManager.Shutdown();

var allSame = identical == dispatches;
int rc = 0;
rc |= Console.Out.NewLine == "\r\n" ? 0 : 1;
rc |= string.IsNullOrEmpty("x") ? 0 : 1;
rc |= string.IsNullOrEmpty("") ? 0 : 1;
return allSame && rc == 0 ? 0 : 1;
}

private static TimeSlice[] GenerateSyntheticSlices(int count)
{
var rng = new Random(7);
var slices = new TimeSlice[count];
var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

for (int i = 0; i < count; i++)
{
double pnl = (rng.NextDouble() - 0.45) * 200.0;
var bar = new TimeSlice(
    t0.AddHours(i).Ticks,
    mtmHigh: Math.Max(100.0, 100.0 + pnl * 5.0),
    mtmLow: Math.Max(1.0, 100.0 - pnl * 5.0),
    pnl: pnl,
    eod: (byte)(i % 24 == 23 ? 1 : 0)
);
slices[i] = bar;
}

return slices;
}

private static long Signature(IReadOnlyList<Trade> trades)
{
unchecked
{
long h = 17;
foreach (var t in trades)
{
h = h * 31 + (t.Side?.GetHashCode() ?? 0);
h = h * 31 + (t.Quantity?.GetHashCode() ?? 0);
h = h * 31 + (t.Open?.Ticks ?? 0);
h = h * 31 + (t.Close?.Ticks ?? 0);
h = h * 31 + BitConverter.DoubleToInt64Bits(t.EntryPrice);
h = h * 31 + BitConverter.DoubleToInt64Bits(t.ExitPrice);
}
return h;
}
}
}
