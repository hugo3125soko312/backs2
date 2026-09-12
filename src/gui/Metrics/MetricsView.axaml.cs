using Avalonia;
using Avalonia.Controls;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class MetricsView : UserControl
{
    public MetricsView()
    {
        InitializeComponent();

        // Resolve the ViewModel from the DI container
        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<MetricsViewModel>();
        }
    }
}