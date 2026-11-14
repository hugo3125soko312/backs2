using System;
using System.Globalization;
using System.Windows.Data;

namespace backs2.Views
{
    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b) return System.Windows.Visibility.Visible;
            return System.Windows.Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is System.Windows.Visibility v && v == System.Windows.Visibility.Visible) return true;
            return false;
        }
    }
}