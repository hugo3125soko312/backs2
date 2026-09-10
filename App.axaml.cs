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

    private void ConfigureServices(ServiceCollection services)
    {
        // Caching
        services.AddMemoryCache(); // Added: Required for MemoryCache
        services.AddSingleton<CacheManager>();
        
        // Core Services
        services.AddSingleton<LogManager>();
        services.AddSingleton<ConfigManager>();
        services.AddSingleton<IDataProvider, YahooProvider>();
        services.AddSingleton<DataManager>();
        services.AddSingleton<StrategyManager>();
        services.AddSingleton<MetricRegistry>(); // Clean: Only registered once
    
        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<MetricsViewModel>();
        services.AddTransient<StrategyEditorViewModel>();
        services.AddTransient<StratsViewModel>();
    }
}