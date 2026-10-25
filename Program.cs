using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using backs2.Core;
using backs2.Core.Compute;
using backs2.Core.Computing;
using backs2.Core.Computing.Backends;
using backs2.Core.DataAutomation;
using backs2.Core.Metrics;
using backs2.Core.Models;
using backs2.Core.Overview;
using backs2.Core.Runner;

namespace backs2;

public static class Program
{
// WinExe projects do not inherit the parent console on Windows.
// Attaching to the launching terminal makes Console.WriteLine visible
// for CLI-style flags. No-op when the process has no parent console.
[DllImport("kernel32.dll", SetLastError = true)]
private static extern bool AttachConsole(int dwProcessId);

private const int ATTACH_PARENT_PROCESS = -1;

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
var g = nv.Query();
Console.WriteLine(g is null ? "gpu: unavailable (NVML not present or no NVIDIA device)" : "gpu: " + g.Value.ToString("F0") + "% utilization");
return;
}
if (args.Length > 0 && args[0] == "--vram-verify")
{
using var vp = new backs2.Core.Diagnostics.VramProbe();
var q = vp.Query();
if (q.UsedMb is null) Console.WriteLine("vram: unavailable (no DXGI adapter)");
else Console.WriteLine("vram: " + q.UsedMb.Value.ToString("F0") + " MB used of " + q.BudgetMb.Value.ToString("F0") + " MB budget");
return;
}
if (args.Length > 0 && args[0] == "--et-verify")
{
Console.WriteLine("ET mismatches: " + backs2.Core.Utils.EasternTime.VerifyAgainstFramework(500000, 42));
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

private static Task<int> RunMcBenchAsync()
{
var cpuBackend = new CpuBackend(new SimulationKernelCpu());
var gpuBackend = new GpuBackend(cpuBackend);
var rng = new Random(11);
int versions = 240;
int iters = 100;
var perVersion = new TimeSlice[versions][];
for (int v = 0; v < versions; v++)
{
int n = 1500 + rng.Next(0, 900);
var sl = new TimeSlice[n];
long t0 = 1_600_000_000_000L;
for (int s = 0; s < n; s++)
sl[s] = new TimeSlice(t0 + s * 1_800_000L, rng.NextDouble() * 60, -rng.NextDouble() * 60, (rng.NextDouble() - 0.5) * 30, (byte)(s % 13 == 0 ? 1 : 0));
perVersion[v] = sl;
}
var sim = new MonteCarloSimulationRequest(180, 100_000, 3_000, 10_000, 8_000, 5, true, false);
var reqs = new List<MonteCarloDispatchRequest>();
for (int v = 0; v < versions; v++)
reqs.Add(new MonteCarloDispatchRequest(sim, perVersion[v], iters, Seed: 5000 + v, RetainCurves: false, MaxDegreeOfParallelism: 1));
var cpuDisp = new CpuComputeDispatcher(new List<IComputeBackend> { cpuBackend }, null);
var gpuDisp = new CpuComputeDispatcher(new List<IComputeBackend> { cpuBackend, gpuBackend }, null);
var sw = System.Diagnostics.Stopwatch.StartNew();
foreach (var r in reqs) cpuDisp.DispatchMonteCarloAsync(r, default).GetAwaiter().GetResult();
sw.Stop(); long cpuPerVersion = sw.ElapsedMilliseconds;
Console.WriteLine("cpu per-version: " + cpuPerVersion + " ms");
sw.Restart();
cpuDisp.DispatchMonteCarloBatchAsync(reqs, default).GetAwaiter().GetResult();
sw.Stop(); long cpuBatch = sw.ElapsedMilliseconds;
Console.WriteLine("cpu batch: " + cpuBatch + " ms");
sw.Restart();
var gpuRes = gpuDisp.DispatchMonteCarloBatchAsync(reqs, default).GetAwaiter().GetResult();
sw.Stop(); long gpuBatch = sw.ElapsedMilliseconds;
Console.WriteLine("gpu batch: " + gpuBatch + " ms (backend=" + gpuDisp.Backend.Name + ")");
Console.WriteLine("gpu batch total iterations: " + gpuRes.Length + " versions x " + (gpuRes.Length > 0 ? gpuRes[0].Count : 0));
return Task.FromResult(0);
}

private static Task<int> VerifyBatchAsync()
{
var cpuBackend = new CpuBackend(new SimulationKernelCpu());
var backends = new List<IComputeBackend> { cpuBackend, new GpuBackend(cpuBackend) };
var cfg = new ConfigManager();
cfg.Settings.MonteCarloBackend = "Auto";
var dispatcher = new CpuComputeDispatcher(backends, cfg);
Console.WriteLine("resolved backend: " + dispatcher.Backend.Name);
var rng = new Random(7);
var requests = new List<MonteCarloDispatchRequest>();
for (int v = 0; v < 24; v++)
{
int sliceCount = 200 + rng.Next(0, 600);
var slices = new TimeSlice[sliceCount];
long t0 = 1_600_000_000_000L;
for (int s = 0; s < sliceCount; s++)
{
slices[s] = new TimeSlice(t0 + s * 60_000L, rng.NextDouble() * 50, -rng.NextDouble() * 50, (rng.NextDouble() - 0.5) * 20, (byte)(s % 390 == 0 ? 1 : 0));
}
var sim = new MonteCarloSimulationRequest(180, 100_000, 3_000, 10_000, 8_000, 5, true, false);
requests.Add(new MonteCarloDispatchRequest(sim, slices, 100, Seed: 1234, RetainCurves: false, MaxDegreeOfParallelism: -1));
}
int mismatches = BatchDeterminismCheck.Run(dispatcher, requests, 1234);
Console.WriteLine("backend after run: " + dispatcher.Backend.Name);
Console.WriteLine("batch determinism mismatches: " + mismatches);
return Task.FromResult(mismatches == 0 ? 0 : 1);
}

private static async Task RunComputeSelfTest(string[] args)
{
int memMb = 256;
bool cancel = false;

foreach (var a in args)
{
if (a.StartsWith("--mem=", StringComparison.Ordinal))
int.TryParse(a.Substring(6), out memMb);
else if (a == "--cancel")
cancel = true;
}

var manager = new ComputeWorkerManager();

var config = new JobConfig(
ScriptPath: "",
CachePath: "",
Params: new Dictionary<string, string>(),
MemoryLimitMb: memMb);

Console.WriteLine("[selftest] memcap=" + memMb + " MB  cancel=" + cancel);

using var cts = new CancellationTokenSource();
int frameCount = 0;

var progress = new Progress<JobProgress>(p =>
{
frameCount++;
Console.WriteLine("[selftest] frame #" + frameCount + ": " + p.Phase + " " +
p.SlicesElapsed + "/" + p.TotalSlices + " (" +
p.PercentComplete.ToString("F1") + "%)");

if (cancel && frameCount == 2)
{
Console.WriteLine("[selftest] cancel requested after frame #2");
cts.Cancel();
}
});

try
{
var result = await manager.RunJobAsync(config, progress, cts.Token);
Console.WriteLine("[selftest] result: trades=" + result.TradeCount +
" summary='" + result.Summary + "'");
Console.WriteLine("[selftest] PASS — " + frameCount + " progress frame(s)");
}
catch (OperationCanceledException)
{
Console.WriteLine("[selftest] CANCELLED — " + frameCount +
" progress frame(s) received before cancel");
}
catch (Exception ex)
{
Console.WriteLine("[selftest] FAIL — " + ex.GetType().Name + ": " + ex.Message);
Environment.ExitCode = 1;
}
}

// ------------------------------------------------------------------
// Phase 26B.1 — StrategyManager parallelism verification
// ------------------------------------------------------------------

/// <summary>
/// Picks the first strategy in data/strategies/ that actually produces
/// trades on synthetic data, then runs it N times serially (semaphore
/// in place, baseline) followed by N times concurrently with the
/// semaphore bypassed. If every parallel run produces a byte-identical
/// trade list to the baseline, the compiled ScriptRunner is confirmed
/// safe for concurrent invocation. Exit code 0 on PASS, 1 on FAIL.
///
/// A strategy that emits zero trades produces a trivially-matching
/// signature and proves nothing — the candidate loop rejects those.
/// </summary>
private static async Task<int> VerifyStrategyParallelismAsync()
{
const int RunsPerMode = 4;
const int BarCount = 5000;
const int MinTradesForSignal = 3;

Console.WriteLine("[verify] starting");

const string strategiesDir = "data/strategies";
if (!Directory.Exists(strategiesDir))
{
Console.WriteLine("[verify] data/strategies directory not found");
return 1;
}

var candidates = Directory.GetFiles(strategiesDir, "*.cs");
if (candidates.Length == 0)
{
Console.WriteLine("[verify] no strategies in data/strategies/");
return 1;
}

var data = GenerateSyntheticKlines(BarCount);
Console.WriteLine("[verify] synthetic data: " + BarCount + " bars");

var registry = new MetricRegistry();

// --- Pick a strategy that actually trades on this data ---
string? chosenPath = null;
string chosenSource = "";
Dictionary<string, string> chosenDefaults = new();
int chosenTradeCount = 0;

foreach (var candidate in candidates)
{
string source = await File.ReadAllTextAsync(candidate);
var declared = StrategyParameterParser.Parse(source);
var defaults = new Dictionary<string, string>();
foreach (var p in declared)
defaults[p.Name] = p.DefaultValue;

var probeStrat = new StrategyManager(registry) { SerializeBacktests = true };
var probe = await probeStrat.RunBacktestFromFileAsync(candidate, data, defaults);

int trades = probe.Success ? probe.Trades.Count : 0;
Console.WriteLine("[verify] candidate " + Path.GetFileName(candidate) + ": " +
(probe.Success ? trades + " trade(s)" : "FAILED — " + probe.Error));

if (probe.Success && trades >= MinTradesForSignal)
{
chosenPath = candidate;
chosenSource = source;
chosenDefaults = defaults;
chosenTradeCount = trades;
break;
}
}

if (chosenPath == null)
{
Console.WriteLine("[verify] FAIL — no strategy in data/strategies/ produced >= " +
MinTradesForSignal + " trade(s) on synthetic data.");
Console.WriteLine("[verify] A 0-trade result matches trivially and does not test concurrency safety.");
Console.WriteLine("[verify] Add a strategy that trades on a random walk, or feed real cached bars instead of synthetic.");
return 1;
}

Console.WriteLine("[verify] chosen strategy: " + Path.GetFileName(chosenPath) +
" (" + chosenTradeCount + " trade(s), " +
chosenDefaults.Count + " param(s) defaulted)");

// --- Serial baseline ---
var strat = new StrategyManager(registry) { SerializeBacktests = true };

var baseline = await strat.RunBacktestFromFileAsync(chosenPath, data, chosenDefaults);
if (!baseline.Success)
{
Console.WriteLine("[verify] baseline failed: " + baseline.Error);
return 1;
}

long baselineSig = Signature(baseline.Trades);
Console.WriteLine("[verify] baseline: " + baseline.Trades.Count +
" trade(s), sig=" + baselineSig);

int serialMatches = 0;
for (int i = 0; i < RunsPerMode; i++)
{
var r = await strat.RunBacktestFromFileAsync(chosenPath, data, chosenDefaults);
if (r.Success && Signature(r.Trades) == baselineSig) serialMatches++;
}
Console.WriteLine("[verify] serial: " + serialMatches + "/" + RunsPerMode + " match baseline");

// --- Parallel: bypass the throttle entirely ---
strat.SerializeBacktests = false;

var tasks = new Task<StrategyRunResult>[RunsPerMode];
for (int i = 0; i < RunsPerMode; i++)
tasks[i] = strat.RunBacktestFromFileAsync(chosenPath, data, chosenDefaults);

StrategyRunResult[] results;
try
{
results = await Task.WhenAll(tasks);
}
catch (Exception ex)
{
Console.WriteLine("[verify] parallel threw: " + ex.GetType().Name + ": " + ex.Message);
return 1;
}

int parallelMatches = 0;
for (int i = 0; i < RunsPerMode; i++)
{
var r = results[i];
if (!r.Success)
{
Console.WriteLine("[verify] parallel #" + i + ": failed — " + r.Error);
continue;
}

long sig = Signature(r.Trades);
if (sig == baselineSig)
{
parallelMatches++;
}
else
{
Console.WriteLine("[verify] parallel #" + i + ": sig mismatch — expected " +
baselineSig + ", got " + sig +
" (" + r.Trades.Count + " trades)");
}
}
Console.WriteLine("[verify] parallel: " + parallelMatches + "/" + RunsPerMode + " match baseline");

bool pass = serialMatches == RunsPerMode && parallelMatches == RunsPerMode;

Console.WriteLine(pass
? "[verify] PASS — ScriptRunner is thread-safe; StrategyManager throttle can be bypassed by the optimizer"
: "[verify] FAIL — ScriptRunner is not safe for concurrent invocation; keep the throttle");

return pass ? 0 : 1;
}

// ------------------------------------------------------------------
// Phase 29B.2 — Diagnostics: Monte Carlo compute path verification
// ------------------------------------------------------------------

/// <summary>
/// Exercises the Monte Carlo compute path out of process so the DirectX 12
/// dispatch path, the backend toggle, log dedupe, and determinism can be
/// exercised without driving the Avalonia UI.
///
/// <para>Kept in tree as a permanent regression canary for the DirectX 12
/// dispatch path and the <c>MonteCarloBackend</c> toggle, mirroring the
/// other diagnostics hooks (<c>--verify-strategy-parallelism</c>,
/// <c>--compute-selftest</c>). It is the only way to count
/// first-chance <c>ArgumentException</c>s out of the process and to
/// exercise the compute path without spinning up the GUI.</para>
/// </summary>
private static async Task<int> VerifyMonteCarloAsync(string[] args)
{
string? mode = null;
int dispatches = 8;
bool warmup = false;
int iterations = 100;
int seed = 12345;
bool fixedSeed = false;

foreach (var a in args)
{
if (a.StartsWith("--backend=", StringComparison.Ordinal)) mode = a.Substring(10);
else if (a.StartsWith("--dispatches=", StringComparison.Ordinal)) int.TryParse(a.Substring(13), out dispatches);
else if (a.StartsWith("--iterations=", StringComparison.Ordinal)) int.TryParse(a.Substring(13), out iterations);
else if (a.StartsWith("--seed=", StringComparison.Ordinal)) int.TryParse(a.Substring(7), out seed);
else if (a == "--fixed-seed") fixedSeed = true;
else if (a == "--warmup") warmup = true;
}

LogManager.Initialize();
LogManager.Info(LogTag.MonteCarlo,
"[29B.2-verify] start backend=" + (mode ?? "(settings.json)") +
" dispatches=" + dispatches + " iterations=" + iterations +
" warmup=" + warmup);

var config = new ConfigManager();
if (mode != null) config.Settings.MonteCarloBackend = mode;

// One backend pair and one dispatcher for the whole run, exactly as the DI
// container registers them — so the first-dispatch gate and the device
// pipeline cache are exercised the way the app exercises them rather than
// being rebuilt per combination.
var cpu = new CpuBackend(new SimulationKernelCpu());
var gpu = new GpuBackend(cpu);
var dispatcher = new CpuComputeDispatcher(new IComputeBackend[] { cpu, gpu }, config);

var slices = BuildSyntheticSlices(256);
const int target = 120;

int exceptions = 0;
AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
{
if (e.Exception is ArgumentException ae && ae.Message.Contains("same key"))
Interlocked.Increment(ref exceptions);
};

// Optional untimed pass: absorbs the device probe, shader compile, and
// pipeline creation so the timed figure is steady-state dispatch cost.
// Note: with warmup the very first serial dispatch populates the gate,
// which is the correct steady-state measurement but does NOT exercise
// the cold-gate race. Run without --warmup to test the race itself.
if (warmup)
{
for (int d = 0; d < 8; d++)
new MonteCarloEngine(dispatcher).Run(slices, target, 100000, "static", 5.0, 10.0, 0.0,
iterations: iterations, seed: seed + d, maxDegreeOfParallelism: 1, retainCurves: false);
}

// One engine per combination — the optimizer's shape: N independent coarse
// Monte Carlo passes sharing the process-wide compute backend.
var engines = new MonteCarloEngine[dispatches];
for (int d = 0; d < dispatches; d++)
engines[d] = new MonteCarloEngine(dispatcher);

var sw = System.Diagnostics.Stopwatch.StartNew();

var work = new List<Task<(long Sig, int Count)>>();
for (int d = 0; d < dispatches; d++)
{
int local = d;
work.Add(Task.Run(() =>
{
var r = engines[local].Run(slices, target, 100000, "static", 5.0, 10.0, 0.0,
iterations: iterations, seed: fixedSeed ? seed : seed + local,
maxDegreeOfParallelism: 1, retainCurves: false);
return (Signature(r.TerminalBalances), r.Iterations);
}));
}

var results = await Task.WhenAll(work);
sw.Stop();

var first = results[0];
int identical = 0;
foreach (var r in results) if (r.Sig == first.Sig && r.Count == first.Count) identical++;

LogManager.Info(LogTag.MonteCarlo,
"[29B.2-verify] backend=" + (mode ?? "(settings.json)") +
" dispatches=" + dispatches + " iterations=" + iterations +
" warmup=" + warmup + " wall=" + sw.ElapsedMilliseconds + "ms" +
" perDispatch=" + (sw.Elapsed.TotalMilliseconds / dispatches).ToString("F3") + "ms");
LogManager.Info(LogTag.MonteCarlo,
"[29B.2-verify] settings.MonteCarloBackend='" + config.Settings.MonteCarloBackend + "'" +
" resolved=" + dispatcher.Backend.Name +
" duplicateKeyFirstChanceExceptions=" + exceptions);
LogManager.Info(LogTag.MonteCarlo,
"[29B.2-verify] resultCount=" + first.Count +
" identicalToFirst=" + identical + "/" + results.Length +
" sig0=" + first.Sig);

LogManager.Shutdown();

// Determinism is only assertable within one backend: the GPU uses Philox
// and the CPU uses System.Random, so cross-backend equality is not the
// contract (Phase 29B settled on statistical equivalence).
return exceptions == 0 ? 0 : 1;
}

private static TimeSlice[] BuildSyntheticSlices(int count)
{
var rng = new Random(7);
var slices = new TimeSlice[count];
var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

for (int i = 0; i < count; i++)
{
double pnl = (rng.NextDouble() - 0.45) * 200.0;
double mtmHigh = rng.NextDouble() * 150.0;
double mtmLow = -rng.NextDouble() * 150.0;
byte eod = (byte)(i % 24 == 23 ? 1 : 0);

slices[i] = new TimeSlice(t0.AddHours(i).Ticks, mtmHigh, mtmLow, pnl, eod);
}

return slices;
}

private static long Signature(double[] values)
{
unchecked
{
long h = 17;
foreach (var v in values)
h = h * 31 + BitConverter.DoubleToInt64Bits(v);
return h;
}
}

private static List<Kline> GenerateSyntheticKlines(int count)
{
var rng = new Random(42);
var list = new List<Kline>(count);
double price = 100.0;
var t0 = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

for (int i = 0; i < count; i++)
{
double open = price;
double change = (rng.NextDouble() - 0.5) * 2.0;
double close = open + change;
double high = Math.Max(open, close) + rng.NextDouble() * 0.5;
double low = Math.Min(open, close) - rng.NextDouble() * 0.5;
long vol = 100 + rng.Next(900);

list.Add(new Kline(t0.AddDays(i), open, high, low, close, vol));
price = close;
}

return list;
}

private static long Signature(IReadOnlyList<Trade> trades)
{
unchecked
{
long h = 17;
foreach (var t in trades)
{
h = h * 31 + (t.Side?.GetHashCode() ?? 0);
h = h * 31 + t.EntryIndex;
h = h * 31 + t.ExitIndex;
h = h * 31 + BitConverter.DoubleToInt64Bits(t.EntryPrice);
h = h * 31 + BitConverter.DoubleToInt64Bits(t.ExitPrice);
}
return h;
}
}
}