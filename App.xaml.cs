using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core.Compute;
using backs2.Core.Computing;
using backs2.Core.Computing.Backends;
using backs2.Core.DataAutomation;
using backs2.Core.Diagnostics;
using backs2.Core.MT5;
using backs2.Core.Optimizer;
using backs2.Core.Providers;
using backs2.Services;
using backs2.Theme;
using backs2.ViewModels;
using backs2.Views;
using Microsoft.Extensions.DependencyInjection;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.Core.Data;
using backs2.Core.Diagnostics;
using backs2.Core.Optimizer;
using backs2.Core.Runner;
using backs2.Services;
using backs2.Theme;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.Core.Data;
using backs2.Core.Diagnostics;
using backs2.Core.Optimizer;
using backs2.Core.Runner;
using backs2.Theme;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.Core.Data;
using backs2.Core.Diagnostics;
using backs2.Core.Optimizer;
using backs2.Core.Runner;
using backs2.Theme;

namespace backs2;

public partial class App : Application
{
    public static ServiceProvider ServiceProvider { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        
        var services = new ServiceCollection();
        ConfigureServices(services);
        ServiceProvider = services.BuildServiceProvider();
        
        var mainWindow = new MainWindow
        {
            DataContext = ServiceProvider.GetRequiredService<MainViewModel>()
        };
        mainWindow.Show();
    }

    private void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<StrategyCatalogService>();
        services.AddSingleton<StrategyManager>();
        services.AddSingleton<DataManager>();
        services.AddSingleton<PresetService>();
        services.AddSingleton<LastRunStore>();
        services.AddSingleton<SelectionQueue>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<ConfigManager>();
        services.AddSingleton<OptimizationRunner>();
        services.AddSingleton<OptimizationStore>();
        services.AddSingleton<WalkForwardStore>();
        services.AddSingleton<WalkForwardRunner>();
        services.AddSingleton<PerfSampler>();
        services.AddSingleton<MetricRegistry>();
        services.AddSingleton<CacheManager>();
        services.AddSingleton<backs2.Core.Data.IQueueOriginGate, backs2.Core.Data.QueueOriginGate>();
        services.AddSingleton<backs2.Core.Data.IDataOriginResolver, backs2.Core.Data.DataOriginResolver>();
        services.AddSingleton<backs2.Core.Data.ITickCompiler, backs2.Core.Data.Mt5TickCompiler>();
        services.AddSingleton<Core.DataAutomation.IStreamingLoaderFactory, Core.GuiLoading.StreamingLoaderFactory>();
        services.AddSingleton<MonteCarloEngine>();
        services.AddSingleton<ComputeWorkerManager>();
        services.AddSingleton<SimulationKernelCpu>();
        services.AddSingleton<CpuBackend>(sp => new CpuBackend());
        services.AddSingleton<GpuBackend>(sp => new GpuBackend());
        services.AddSingleton<IComputeBackend>(sp => sp.GetRequiredService<CpuBackend>());
        services.AddSingleton<IComputeBackend>(sp => sp.GetRequiredService<GpuBackend>());
        services.AddSingleton<IComputeDispatcher, CpuComputeDispatcher>();
        services.AddSingleton<DataAutomationService>();
        services.AddSingleton<OcrWorkerClient>();
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
    }
}
