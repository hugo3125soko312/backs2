using Avalonia.Controls;
using backs2.ViewModels;

namespace backs2.Views;

public partial class PresetPickerWindow : Window
{
    public PresetPickerWindow()
    {
        InitializeComponent();
    }

    public PresetPickerWindow(PresetPickerViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += (_, _) => Close(vm.Result);
    }
}