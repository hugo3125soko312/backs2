using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using backs2.Core;
using backs2.ViewModels;
using backs2.Views;

namespace backs2;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // 1. Initialize Managers (The DI Composition Root)
        // Order matters: LogManager first so others can use it, ConfigManager second
        var logManager = new LogManager();
        var configManager = new ConfigManager();
        var dataManager = new DataManager(configManager);
        var strategyManager = new StrategyManager(configManager);

        // The constructor now expects 4 arguments
        var mainViewModel = new MainViewModel(configManager, dataManager, strategyManager, logManager);

        // 3. Assign the DataContext to the MainWindow
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                // This makes the ViewModels available to the entire UI
                DataContext = mainViewModel 
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
