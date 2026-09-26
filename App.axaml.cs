// App.axaml.cs
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using backs2.Core;
using backs2.Core.Accounts;
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
    }
}