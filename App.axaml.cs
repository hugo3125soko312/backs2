using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
using backs2.Core.Runner;
using backs2.Theme;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Diagnostics;
using backs2.Core.Providers;
using backs2.Core.Metrics;

namespace backs2;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize()
    {
        LogManager.Debug(LogTag.App, "App.Initialize → loading XAML");
        AvaloniaXamlLoader.Load(this);
        LogManager.Debug(LogTag.App, "App.Initialize complete");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        LogManager.Debug(LogTag.App, "OnFrameworkInitializationCompleted → building DI container");

        var appSw = Stopwatch.StartNew();

        var collection = new ServiceCollection();
        ConfigureServices(collection);
        Services = collection.BuildServiceProvider();
        LogManager.Info(LogTag.App, $"[startup] DI container built in {appSw.ElapsedMilliseconds}ms");

        var cfgSw = Stopwatch.StartNew();
        var cfg = Services.GetRequiredService<ConfigManager>();
        LogManager.SetMinimumLevel(cfg.Settings.LogLevel);
        LogManager.Info(LogTag.App, $"[startup] ConfigManager resolved in {cfgSw.ElapsedMilliseconds}ms (LogLevel={cfg.Settings.LogLevel})");

        ThemeApplier.Apply(cfg.Settings);

        var mt5Sw = Stopwatch.StartNew();
        try
        {
            var mt5Server = Services.GetRequiredService<Mt5TickServer>();
            mt5Server.Start();
            LogManager.Info(LogTag.App, $"[startup] MT5 server started in {mt5Sw.ElapsedMilliseconds}ms");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.App, "Failed to start MT5 tick server", ex);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vmSw = Stopwatch.StartNew();
            MainViewModel mainVm;
            try
            {
                mainVm = Services.GetRequiredService<MainViewModel>();
                LogManager.Info(LogTag.App, $"[startup] MainViewModel resolved in {vmSw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.App, "MainViewModel resolution failed", ex);
                throw;
            }

            var winSw = Stopwatch.StartNew();
            try
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = mainVm
                };
                LogManager.Info(LogTag.App, $"[startup] MainWindow constructed in {winSw.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.App, "MainWindow construction failed", ex);
                throw;
            }

            // Phase 36A: start the performance sampler and wire the frame
            // counter. The sampler runs at 1 Hz; OnFrame is a single increment
            // and is safe to call per rendered frame.
            // Phase 36A sampler startup is disabled pending a crash fix.
            // The sampler, thresholds and snapshot types remain in the tree
            // and are exercised by --perf-verify; only the in-app startup is
            // gated off.
        }

        LogManager.Info(LogTag.App, $"[startup] OnFrameworkInitializationCompleted total {appSw.ElapsedMilliseconds}ms");

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddSingleton<backs2.Core.Data.ITickCompiler, backs2.Core.Data.Mt5TickCompiler>();
        services.AddSingleton<backs2.Core.Data.IQueueOriginGate, backs2.Core.Data.QueueOriginGate>();
        services.AddSingleton<backs2.Core.Data.IDataOriginResolver, backs2.Core.Data.DataOriginResolver>();
        services.AddSingleton<CacheManager>();

        services.AddSingleton<ConfigManager>();
        services.AddSingleton<DataManager>();
        services.AddSingleton<MetricRegistry>();
        services.AddSingleton<StrategyManager>();
        services.AddSingleton<StrategyCatalogService>();
        services.AddSingleton<PresetService>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<LastRunStore>();
        services.AddSingleton<MonteCarloEngine>();
        services.AddSingleton<SelectionQueue>();
        services.AddSingleton<backs2.Core.GuiLoading.IStreamingLoaderFactory, backs2.Core.GuiLoading.StreamingLoaderFactory>();

        // Parameter optimizer batch archives
        services.AddSingleton<OptimizationStore>();
        services.AddSingleton<OptimizationRunner>();

        // Compute worker: process-isolated strategy / Monte Carlo execution.
        services.AddSingleton<ComputeWorkerManager>();

        // Unified computing subsystem (Phase 28A, GPU path Phase 29B). The
        // dispatcher resolves every registered backend and picks the accelerated
        // one when the device probe succeeded, so CPU-vs-GPU selection lives in
        // the dispatcher rather than in every caller. Both backends share the one
        // SimulationKernelCpu instance: it is stateless, so a fresh instance
        // would only add a second copy of the same object.
        services.AddSingleton<SimulationKernelCpu>();
        services.AddSingleton<CpuBackend>(sp =>
            new CpuBackend(sp.GetRequiredService<SimulationKernelCpu>()));
        services.AddSingleton<GpuBackend>(sp =>
            new GpuBackend(sp.GetRequiredService<CpuBackend>()));
        services.AddSingleton<IComputeBackend>(sp => sp.GetRequiredService<CpuBackend>());
        services.AddSingleton<IComputeBackend>(sp => sp.GetRequiredService<GpuBackend>());
        services.AddSingleton<IComputeDispatcher, CpuComputeDispatcher>();

        // Data Automation
        services.AddSingleton<DataAutomationService>();
        services.AddSingleton<OcrWorkerClient>();

        // MT5 raw tick data
        services.AddSingleton<Mt5TickServer>();
        services.AddSingleton<Mt5TickService>();

        // Runtime performance monitor (ROADMAP Phase 36A).
        services.AddSingleton<PerfSampler>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<ChartViewModel>();
        services.AddTransient<DataViewModel>();
        services.AddTransient<MetricsViewModel>();
        services.AddTransient<RunnerViewModel>();
        services.AddTransient<AccountViewModel>();
        services.AddTransient<OverviewViewModel>();
        services.AddTransient<MonteCarloViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<ConfigViewModel>();
        services.AddTransient<ProvidersViewModel>();
        services.AddTransient<DataAutomationViewModel>();
        services.AddTransient<Mt5TickViewModel>();

        services.AddHttpClient("ResilientClient")
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddKeyedTransient<IDataProvider, TwelveDataProvider>("Twelve Data");
        services.AddKeyedTransient<IDataProvider, TiingoProvider>("Tiingo");
        services.AddKeyedTransient<IDataProvider, YahooProvider>("Yahoo");
        services.AddKeyedTransient<IDataProvider, MassiveProvider>("Massive");
        services.AddKeyedTransient<IDataProvider, BinanceProvider>("Binance");
        services.AddKeyedTransient<IDataProvider, FinnhubProvider>("Finnhub");
        services.AddKeyedTransient<IDataProvider, FcsApiProvider>("FCS API");
    }
}