using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using backs2.ViewModels;

namespace backs2.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();

        // Attach in the tunnel phase so our handler runs before the TextBox's
        // internal Enter handling. On bubble the TextBox inserts a newline
        // first and marks the event handled, so we never see the key.
        CommandInputBox.AddHandler(
            InputElement.KeyDownEvent,
            OnCommandInputKeyDown,
            RoutingStrategies.Tunnel);

        // The ViewModel skips its flush while this view is off screen: flushing
        // rebuilds the whole LogText string and rebinds the TextBox, which is
        // wasted work while the user is on another tab and was the dominant UI
        // cost during an optimizer run.
        AttachedToVisualTree += (_, _) =>
        {
            if (DataContext is LogsViewModel vm) vm.SetVisible(true);
        };

        DetachedFromVisualTree += (_, _) =>
        {
            if (DataContext is LogsViewModel vm) vm.SetVisible(false);
        };
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