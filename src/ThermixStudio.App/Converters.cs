using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using ThermixStudio.Core;

namespace ThermixStudio.App;

public static class Converters
{
    public static readonly FileNameValueConverter FileNameConverter = new();
    public static readonly NullToVisibilityValueConverter NullToVisibilityConverter = new();
}

public sealed class FileNameValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string path && !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Returns Visible when value is null (for placeholder), Collapsed otherwise.</summary>
public sealed class NullToVisibilityValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CriticalityToPortugueseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not EquipmentCriticality criticality)
        {
            return string.Empty;
        }

        return criticality switch
        {
            EquipmentCriticality.Low => "Baixa",
            EquipmentCriticality.Medium => "Media",
            EquipmentCriticality.High => "Alta",
            EquipmentCriticality.Critical => "Critica",
            _ => criticality.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

public sealed class BooleanToVisibilityValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility visibility && visibility == Visibility.Visible;
}

public sealed class ImageViewModeToPortugueseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ViewModels.ImageViewMode mode)
        {
            return string.Empty;
        }

        return mode switch
        {
            ViewModels.ImageViewMode.Original => "Original da Câmera",
            ViewModels.ImageViewMode.Thermal => "Térmica (IV)",
            ViewModels.ImageViewMode.Visible => "Luz Visível",
            ViewModels.ImageViewMode.Fusion => "Combinação Térmica (Intervalo)",
            ViewModels.ImageViewMode.Blending => "Combinação Térmica",
            ViewModels.ImageViewMode.PiP => "Imagem na imagem",
            ViewModels.ImageViewMode.Msx => "MSX",
            _ => mode.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class IsothermModeToPortugueseConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ViewModels.IsothermMode mode)
        {
            return string.Empty;
        }

        return mode switch
        {
            ViewModels.IsothermMode.Above => "Acima",
            ViewModels.IsothermMode.Below => "Abaixo",
            ViewModels.IsothermMode.Interval => "Intervalo",
            ViewModels.IsothermMode.Humidity => "Umidade",
            ViewModels.IsothermMode.Insulation => "Insulação",
            ViewModels.IsothermMode.Custom => "Personalizada",
            _ => mode.ToString()
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
