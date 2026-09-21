using Avalonia;
using Avalonia.Controls;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class StratsView : UserControl
{
    public StratsView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<StratsViewModel>();
        }
    }
}