using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia.Media.Imaging;

namespace ImageZoneEditor.Models;

/// <summary>
/// Область (зона) на изображении. Координаты и размеры хранятся
/// в пикселях оригинального изображения.
/// </summary>
public class Zone : INotifyPropertyChanged
{
    private string _name = "Новая зона";
    private double _x;
    private double _y;
    private double _width;
    private double _height;
    private double _angle;
    private bool _isSelected;
    private ZoneFilterSettings _filters = new();
    private Bitmap? _preview;

    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public double X
    {
        get => _x;
        set => SetField(ref _x, value);
    }

    public double Y
    {
        get => _y;
        set => SetField(ref _y, value);
    }

    public double Width
    {
        get => _width;
        set => SetField(ref _width, value);
    }

    public double Height
    {
        get => _height;
        set => SetField(ref _height, value);
    }

    /// <summary>Угол поворота в градусах (по часовой стрелке).</summary>
    public double Angle
    {
        get => _angle;
        set => SetField(ref _angle, value);
    }

    /// <summary>
    /// Фильтры и цветокоррекция зоны. Применяются к превью и при экспорте;
    /// исходное изображение не изменяется.
    /// </summary>
    public ZoneFilterSettings Filters
    {
        get => _filters;
        set => SetField(ref _filters, value ?? new ZoneFilterSettings());
    }

    /// <summary>
    /// Готовое превью зоны с фильтрами. Хранится только в памяти
    /// и не попадает в JSON.
    /// </summary>
    [JsonIgnore]
    public Bitmap? Preview
    {
        get => _preview;
        set
        {
            if (SetField(ref _preview, value))
                OnPropertyChanged(nameof(HasPreview));
        }
    }

    [JsonIgnore]
    public bool HasPreview => _preview is not null;

    /// <summary>Выделена ли зона в редакторе. В JSON не сохраняется.</summary>
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
