using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection; // Important
using backs2.ViewModels;

namespace backs2.Views;

public partial class MetricsView : UserControl
{
    public MetricsView()
    {
        InitializeComponent();
        
        // Resolve the ViewModel from the DI container initialized in App.axaml.cs
        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<MetricsViewModel>();
        }
    }
}