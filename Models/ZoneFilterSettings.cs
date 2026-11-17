using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ImageZoneEditor.Models;

/// <summary>
/// Настройки фильтров и цветокоррекции для одной зоны.
/// Сохраняются в JSON вместе с зоной; пиксели исходного изображения не меняются.
/// </summary>
public class ZoneFilterSettings : INotifyPropertyChanged
{
    private bool _enabled = true;
    private double _brightness;
    private double _contrast;
    private double _saturation;
    private double _hue;
    private double _gamma = 1.0;
    private bool _grayscale;
    private bool _sepia;
    private bool _invert;
    private double _blur;
    private double _sharpen;
    private double _vignette;

    /// <summary>Мастер-выключатель: позволяет отключить фильтры, сохранив их значения.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    /// <summary>Яркость, −100…100. 0 — без изменений.</summary>
    public double Brightness
    {
        get => _brightness;
        set => SetField(ref _brightness, value);
    }

    /// <summary>Контраст, −100…100. 0 — без изменений.</summary>
    public double Contrast
    {
        get => _contrast;
        set => SetField(ref _contrast, value);
    }

    /// <summary>Насыщенность, −100…100. 0 — без изменений.</summary>
    public double Saturation
    {
        get => _saturation;
        set => SetField(ref _saturation, value);
    }

    /// <summary>Сдвиг оттенка в градусах, −180…180. 0 — без изменений.</summary>
    public double Hue
    {
        get => _hue;
        set => SetField(ref _hue, value);
    }

    /// <summary>Гамма, 0.2…3. 1 — без изменений.</summary>
    public double Gamma
    {
        get => _gamma;
        set => SetField(ref _gamma, value);
    }

    public bool Grayscale
    {
        get => _grayscale;
        set => SetField(ref _grayscale, value);
    }

    public bool Sepia
    {
        get => _sepia;
        set => SetField(ref _sepia, value);
    }

    public bool Invert
    {
        get => _invert;
        set => SetField(ref _invert, value);
    }

    /// <summary>Радиус гауссова размытия, 0…20. 0 — без размытия.</summary>
    public double Blur
    {
        get => _blur;
        set => SetField(ref _blur, value);
    }

    /// <summary>Сила резкости, 0…100. 0 — без изменений.</summary>
    public double Sharpen
    {
        get => _sharpen;
        set => SetField(ref _sharpen, value);
    }

    /// <summary>Сила виньетки, 0…100. 0 — без изменений.</summary>
    public double Vignette
    {
        get => _vignette;
        set => SetField(ref _vignette, value);
    }

    /// <summary>
    /// Есть ли хотя бы один активный фильтр, который изменит изображение.
    /// В JSON не сохраняется.
    /// </summary>
    [JsonIgnore]
    public bool HasAny =>
        Enabled &&
        (Math.Abs(Brightness) > 0.001 ||
         Math.Abs(Contrast) > 0.001 ||
         Math.Abs(Saturation) > 0.001 ||
         Math.Abs(Hue) > 0.001 ||
         Math.Abs(Gamma - 1.0) > 0.001 ||
         Grayscale ||
         Sepia ||
         Invert ||
         Blur > 0.001 ||
         Sharpen > 0.001 ||
         Vignette > 0.001);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Возвращает настройки к нейтральным значениям.</summary>
    public void Reset()
    {
        Enabled = true;
        Brightness = 0;
        Contrast = 0;
        Saturation = 0;
        Hue = 0;
        Gamma = 1.0;
        Grayscale = false;
        Sepia = false;
        Invert = false;
        Blur = 0;
        Sharpen = 0;
        Vignette = 0;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAny)));
    }
}
