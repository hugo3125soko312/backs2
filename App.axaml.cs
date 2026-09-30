using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.Core.DataAutomation;
using backs2.Core.MT5;
using backs2.Core.Overview;
using backs2.Core.Runner;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using System;
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

        var collection = new ServiceCollection();
        ConfigureServices(collection);
        Services = collection.BuildServiceProvider();

        var cfg = Services.GetRequiredService<ConfigManager>();
        LogManager.SetMinimumLevel(cfg.Settings.LogLevel);
        LogManager.Info(LogTag.App, $"Log level set to {cfg.Settings.LogLevel}");

        // Start the MT5 TCP listener before any view is constructed,
        // so the EA can connect as soon as the window appears.
        try
        {
            var mt5Server = Services.GetRequiredService<Mt5TickServer>();
            mt5Server.Start();
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.App, "Failed to start MT5 tick server", ex);
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = new MainWindow
                {
                    DataContext = Services.GetRequiredService<MainViewModel>()
                };
                LogManager.Info(LogTag.App, "MainWindow constructed");
            }
            catch (Exception ex)
            {
                LogManager.Error(LogTag.App, "MainWindow construction failed", ex);
                throw;
            }
        }

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