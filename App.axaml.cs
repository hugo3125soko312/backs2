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
                // Resolve the MainViewModel from the container
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
    services.AddSingleton<StrategyManager>();
    services.AddSingleton<MetricRegistry>(); 
    
    // ViewModels
    services.AddTransient<MainViewModel>();
    services.AddTransient<MetricsViewModel>();
    services.AddTransient<StrategyEditorViewModel>();
    services.AddTransient<StratsViewModel>();

    // 1. Register the underlying HttpClientFactory
    services.AddHttpClient("ResilientClient")
        .AddStandardResilienceHandler(options => 
        {
            // Resolve settings from the Singleton ConfigManager
            var sp = services.BuildServiceProvider();
            var cfg = sp.GetRequiredService<ConfigManager>();
            
            options.Retry.MaxRetryAttempts = cfg.Settings.MaxRetries;
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(cfg.Settings.TimeoutSeconds);
        });

    // 2. Register your providers as Keyed Services
    // We use AddKeyedTransient because they depend on injected services (HttpClient)
    services.AddKeyedTransient<IDataProvider, MassiveProvider>("Massive");
    services.AddKeyedTransient<IDataProvider, BinanceProvider>("Binance");
    
    // If you still need a default provider for the old constructor-injection style:
    services.AddSingleton<IDataProvider, YahooProvider>();
    }
}