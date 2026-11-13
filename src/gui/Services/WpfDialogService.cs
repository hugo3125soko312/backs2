using System.Windows;
using Microsoft.Win32;

namespace backs2.Services;

public class WpfDialogService : IDialogService
{
    public void ShowWarning(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public void ShowError(string title, string message)
    {
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public string[]? OpenFileDialog(string filter, string title)
    {
        var dialog = new OpenFileDialog { Filter = filter, Title = title, Multiselect = true };
        if (dialog.ShowDialog() == true) return dialog.FileNames;
        return null;
    }

    public string? SaveFileDialog(string filter, string title)
    {
        var dialog = new SaveFileDialog { Filter = filter, Title = title };
        if (dialog.ShowDialog() == true) return dialog.FileName;
        return null;
    }

    public bool ShowModal(object viewModel)
    {
        Window? win = null;
        var vmName = viewModel.GetType().Name;

        if (vmName == "ColorPickerViewModel") 
            win = new Views.ColorPickerWindow { DataContext = viewModel };
        else if (vmName == "PresetPickerViewModel")
            win = new Views.PresetPickerWindow { DataContext = viewModel };
        else if (vmName == "QueueViewModel")
            win = new Views.QueueWindow { DataContext = viewModel };
        else if (vmName == "StrategySourceViewModel")
            win = new Views.StrategySourceWindow { DataContext = viewModel };

        if (win != null)
        {
            win.Owner = Application.Current.MainWindow;
            return win.ShowDialog() == true;
        }
        return false;
    }
}
