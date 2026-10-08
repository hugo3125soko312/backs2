using Avalonia.Controls;
using Avalonia.Interactivity;

namespace backs2.Views;

public partial class StrategySourceWindow : Window
{
    public StrategySourceWindow()
    {
        InitializeComponent();
    }

    public void Initialize(string name, string source)
    {
        Title = $"Source — {name}";
        SourceBox.Text = source;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}