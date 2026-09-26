using Avalonia.Controls;
using backs2.ViewModels;

namespace backs2.Views;

public partial class QueueWindow : Window
{
    public QueueWindow()
    {
        InitializeComponent();
    }

    public QueueWindow(QueueViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += (_, _) => Close(vm.Committed);
    }
}