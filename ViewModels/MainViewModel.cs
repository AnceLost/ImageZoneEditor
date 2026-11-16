using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageZoneEditor.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace ImageZoneEditor.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty] private Bitmap? _currentImage;
    [ObservableProperty] private double _zoomLevel = 1.0;
    [ObservableProperty] private double _scaledImageWidth;
    [ObservableProperty] private double _scaledImageHeight;
    public ObservableCollection<Zone> Zones { get; } = new();
    private string? _currentImagePath;
    private int _originalWidth;
    private int _originalHeight;

    public MainWindowViewModel()
    {
        // Проверка аргументов командной строки
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            LoadImage(args[1]);
        }
    }

    [RelayCommand]
    private void LoadImage(string path)
    {
        _currentImagePath = path;
        CurrentImage = new Bitmap(path);

        // Получаем реальные размеры через ImageSharp (для точности)
        using var img = SixLabors.ImageSharp.Image.Load(path);
        _originalWidth = img.Width;
        _originalHeight = img.Height;

        CalculateScaleToFit(1920, 1080); // Передать реальные размеры окна

        // Автозагрузка JSON
        var jsonPath = Path.ChangeExtension(path, ".json");
        if (File.Exists(jsonPath))
        {
            var json = File.ReadAllText(jsonPath);
            var loadedZones = JsonSerializer.Deserialize<List<Zone>>(json);
            if (loadedZones != null)
            {
                Zones.Clear();
                foreach (var z in loadedZones) Zones.Add(z);
            }
        }
    }

    [RelayCommand]
    private void OpenImage()
    {
        // Здесь в будущем будет диалоговое окно выбора файла.
        // Пока просто заглушка:
        Console.WriteLine("Кнопка 'Открыть' нажата!");
    }

    // Метод, который вызывается из окна при выборе файла
    public async Task LoadImageFromPathAsync(string path)
    {
        if (!File.Exists(path)) return;

        _currentImagePath = path;

        // 1. Загружаем картинку для отображения (Avalonia Bitmap)
        CurrentImage = new Bitmap(path);

        // 2. Получаем реальные размеры через ImageSharp (нужно для точных зон)
        var imageInfo = SixLabors.ImageSharp.Image.Identify(path);
        if (imageInfo != null)
        {
            _originalWidth = imageInfo.Width;
            _originalHeight = imageInfo.Height;
        }

        // 3. Вписываем в экран (максимальная сторона)
        // Предполагаем, что окно у нас примерно 1000x700 (можно будет сделать динамически позже)
        CalculateScaleToFit(1000, 700);

        // TODO: Здесь позже добавим автозагрузку JSON
    }

    private void CalculateScaleToFit(double viewportWidth, double viewportHeight)
    {
        // Логика "вписать в экран по максимальной стороне"
        double scaleX = viewportWidth / _originalWidth;
        double scaleY = viewportHeight / _originalHeight;
        ZoomLevel = Math.Min(scaleX, scaleY);

        UpdateScaledSize();
    }

        // Вызывается из MainWindow.axaml.cs при кручении колесика
    public void Zoom(double delta)
    {
        if (CurrentImage == null) return;

        // Если крутим вверх - увеличиваем, вниз - уменьшаем
        double zoomFactor = delta > 0 ? 1.15 : 0.85;
        ZoomLevel *= zoomFactor;

        // Ограничители (чтобы не улететь в бесконечность)
        ZoomLevel = Math.Clamp(ZoomLevel, 0.05, 20.0);

        UpdateScaledSize();
    }

    private void UpdateScaledSize()
    {
        ScaledImageWidth = _originalWidth * ZoomLevel;
        ScaledImageHeight = _originalHeight * ZoomLevel;
    }

    // ЗУМИРОВАНИЕ (вызывается из code-behind окна по событию колесика мыши)
    public void OnMouseWheel(double delta)
    {
        double zoomFactor = delta > 0 ? 1.1 : 0.9;
        ZoomLevel *= zoomFactor;
        UpdateScaledSize();
    }

    // ЭКСПОРТ ЗОН (Обрезка оригинального изображения)
    [RelayCommand]
    private async Task ExportZones()
    {
        if (_currentImagePath == null) return;

        using var image = SixLabors.ImageSharp.Image.Load(_currentImagePath);

        foreach (var zone in Zones)
        {
            // В ImageSharp координаты должны быть целыми числами
            var rect = new Rectangle((int)zone.X, (int)zone.Y, (int)zone.Width, (int)zone.Height);

            // Проверка, чтобы не выйти за края
            rect.Intersect(new Rectangle(0, 0, image.Width, image.Height));

            if (rect.Width > 0 && rect.Height > 0)
            {
                using var cropped = image.Clone(x => x.Crop(rect));
                // Здесь логика поворота, если zone.Angle != 0
                if (zone.Angle != 0)
                {
                    cropped.Mutate(x => x.Rotate((float)zone.Angle));
                }

                cropped.Save($"export_{zone.Name}.png"); // Сохраняем в PNG без потерь
            }
        }
    }
}