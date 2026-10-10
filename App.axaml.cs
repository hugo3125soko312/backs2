using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.Core.Compute;
using backs2.Core.Computing;
using backs2.Core.Computing.Backends;
using backs2.Core.DataAutomation;
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
        }

        LogManager.Info(LogTag.App, $"[startup] OnFrameworkInitializationCompleted total {appSw.ElapsedMilliseconds}ms");

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddMemoryCache();
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

        // Parameter optimizer batch archives
        services.AddSingleton<OptimizationStore>();
        services.AddSingleton<OptimizationRunner>();

        // Compute worker: process-isolated strategy / Monte Carlo execution.
        services.AddSingleton<ComputeWorkerManager>();

        // Unified computing subsystem (Phase 28A). The parent process registers the
        // CPU backend only — GpuBackend is never registered here, because the parent
        // UI process must not hold the GPU. Phase 28B resolves GpuBackend inside the
        // worker process, where the device belongs.
        services.AddSingleton<IComputeBackend, CpuBackend>();
        services.AddSingleton<IComputeDispatcher, CpuComputeDispatcher>();

        // Data Automation
        services.AddSingleton<DataAutomationService>();
        services.AddSingleton<OcrWorkerClient>();

        // MT5 raw tick data
        services.AddSingleton<Mt5TickServer>();
        services.AddSingleton<Mt5TickService>();

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