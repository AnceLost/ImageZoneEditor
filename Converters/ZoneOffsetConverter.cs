using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace ImageZoneEditor.Converters;

/// <summary>
/// Прибавляет заданное смещение к числовому значению.
/// Используется для отрисовки рамки зоны вместе с отступами под маркеры.
/// </summary>
public sealed class ZoneOffsetConverter : IValueConverter
{
    public double Offset { get; set; }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            double d => d + Offset,
            float f => f + Offset,
            int i => i + Offset,
            _ => value
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
