using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Media;

namespace backs2.ViewModels
{
    public partial class ColorPickerViewModel : ObservableObject
    {
        [ObservableProperty]
        private Color? _selectedColor;

        [RelayCommand]
        private void SelectColor(Color color)
        {
            SelectedColor = color;
        }

        public ColorPickerViewModel()
        {
            // Initialize with a default color
            SelectedColor = Colors.White;
        }
    }
}
