using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<LogsViewModel>();
        }

        // Attach in the tunnel phase so our handler runs before the TextBox's
        // internal Enter handling. On bubble the TextBox inserts a newline
        // first and marks the event handled, so we never see the key.
        CommandInputBox.AddHandler(
            InputElement.KeyDownEvent,
            OnCommandInputKeyDown,
            RoutingStrategies.Tunnel);
    }

    private void OnCommandInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (e.KeyModifiers != KeyModifiers.None) return;

        // Enter alone executes. Suppress the newline that AcceptsReturn would insert.
        e.Handled = true;

        if (DataContext is LogsViewModel vm
            && vm.ExecuteCommandCommand.CanExecute(null))
        {
            vm.ExecuteCommandCommand.Execute(null);
        }
    }
}