using Avalonia;
using Avalonia.Controls;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class ConfigView : UserControl 
{
    public ConfigView()
    {
        InitializeComponent();
        
        // Ensure Application is resolved using the full path or correct using
        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<ConfigViewModel>();
        }
    }
}