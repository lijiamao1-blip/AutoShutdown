using System.Globalization;
using System.Windows.Data;

namespace AutoShutdown.App.Presentation;

/// <summary>布尔取反转换器（S-CLOSEUI1）。用于把「忙碌中」反向绑定到按钮可用性。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
