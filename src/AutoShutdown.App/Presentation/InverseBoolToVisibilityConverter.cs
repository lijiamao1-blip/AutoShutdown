using System.Globalization;
using System.Windows.Data;

namespace AutoShutdown.App.Presentation;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string text)
        {
            return string.IsNullOrEmpty(text)
                ? System.Windows.Visibility.Collapsed
                : System.Windows.Visibility.Visible;
        }

        return value is true
            ? System.Windows.Visibility.Collapsed
            : System.Windows.Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
