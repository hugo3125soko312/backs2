using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using backs2.Core;
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
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var collection = new ServiceCollection();
        ConfigureServices(collection);
        Services = collection.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Services.GetRequiredService<MainViewModel>()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // Caching
        services.AddMemoryCache();
        services.AddSingleton<CacheManager>();

        // Core Services
        services.AddSingleton<ConfigManager>();
        services.AddSingleton<LogManager>();
        services.AddSingleton<DataManager>();
        services.AddSingleton<MetricRegistry>();
        services.AddSingleton<StrategyManager>();
        services.AddSingleton<StrategyCatalogService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<ChartViewModel>();
        services.AddTransient<DataViewModel>();
        services.AddTransient<MetricsViewModel>();
        services.AddTransient<StratsViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<ConfigViewModel>();

        // HttpClient Resilience
        services.AddHttpClient("ResilientClient")
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 3;
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });

        // Keyed Providers
        services.AddKeyedTransient<IDataProvider, YahooProvider>("Yahoo");
        services.AddKeyedTransient<IDataProvider, MassiveProvider>("Massive");
        // services.AddKeyedTransient<IDataProvider, BinanceProvider>("Binance");
    }
}