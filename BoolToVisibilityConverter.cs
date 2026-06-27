using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BTAudioTray;

/// <summary>true → Visible, false → Collapsed.</summary>
internal sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is Visibility.Visible;
}
