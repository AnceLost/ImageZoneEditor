using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageZoneEditor.Models;
using ImageZoneEditor.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageZoneEditor.ViewModels;

/// <summary>
/// Основная ViewModel редактора зон.
/// Координаты зон всегда хранятся в пикселях оригинального изображения.
/// </summary>
public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 20.0;
    private const double MinZoneSize = 4.0;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddZoneCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveZonesCommand))]
    private Bitmap? _currentImage;

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private double _scaledImageWidth;

    [ObservableProperty]
    private double _scaledImageHeight;

    [ObservableProperty]
    private int _originalWidth;

    [ObservableProperty]
    private int _originalHeight;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedZoneCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetSelectedZoneFiltersCommand))]
    private Zone? _selectedZone;

    [ObservableProperty]
    private bool _hasSelectedZone;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddZoneCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveZonesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomInCommand))]
    [NotifyCanExecuteChangedFor(nameof(ZoomOutCommand))]
    private bool _hasImage;

    [ObservableProperty]
    private string _statusMessage = "Откройте изображение, чтобы начать работу.";

    [ObservableProperty]
    private string _windowTitle = "Редактор активных зон";

    [ObservableProperty]
    private int _zonesCount;

    [ObservableProperty]
    private bool _hasZones;

    public ObservableCollection<Zone> Zones { get; } = new();

    /// <summary>Нужно ли вписать изображение в окно после следующей раскладки.</summary>
    public bool FitPending { get; set; }

    private string? _currentImagePath;

    // PNG-представление изображения для форматов, которые не умеет читать ImageSharp,
    // но умеет декодировать Avalonia/Skia (например, HEIF при наличии кодека).
    private byte[]? _fallbackImagePng;

    private readonly ZonePreviewRenderer _previewRenderer = new();
    private readonly Dictionary<Zone, CancellationTokenSource> _previewCancellations = new();
    private readonly Dictionary<Zone, (ZoneFilterSettings Settings, PropertyChangedEventHandler Handler)> _filterSubscriptions = new();
    private readonly HashSet<Zone> _trackedZones = new();
    private bool _disposed;

    public MainWindowViewModel()
    {
        Zones.CollectionChanged += OnZonesCollectionChanged;

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            try
            {
                LoadImage(args[1]);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Не удалось открыть изображение: {ex.Message}";
            }
        }
    }

    public string? CurrentImagePath => _currentImagePath;

    private void OnZonesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                foreach (var zone in _trackedZones)
                    UntrackZone(zone);
                _trackedZones.Clear();

                foreach (var zone in Zones)
                    TrackZone(zone);
                break;

            default:
                if (e.OldItems is not null)
                {
                    foreach (Zone zone in e.OldItems)
                    {
                        if (_trackedZones.Remove(zone))
                            UntrackZone(zone);
                    }
                }

                if (e.NewItems is not null)
                {
                    foreach (Zone zone in e.NewItems)
                    {
                        if (_trackedZones.Add(zone))
                            TrackZone(zone);
                    }
                }
                break;
        }

        ZonesCount = Zones.Count;
        HasZones = ZonesCount > 0;
    }

    // ---------------------------------------------------------------------
    // Превью фильтров
    // ---------------------------------------------------------------------

    private void TrackZone(Zone zone)
    {
        zone.PropertyChanged += OnZonePropertyChanged;
        SubscribeFilters(zone);
        SchedulePreview(zone);
    }

    private void UntrackZone(Zone zone)
    {
        zone.PropertyChanged -= OnZonePropertyChanged;

        if (_filterSubscriptions.Remove(zone, out var subscription))
            subscription.Settings.PropertyChanged -= subscription.Handler;

        CancelPreview(zone);

        var preview = zone.Preview;
        zone.Preview = null;
        DisposePreviewBitmap(preview);
    }

    private void SubscribeFilters(Zone zone)
    {
        if (_filterSubscriptions.Remove(zone, out var previous))
            previous.Settings.PropertyChanged -= previous.Handler;

        var filters = zone.Filters;
        if (filters is null)
            return;

        PropertyChangedEventHandler handler = (_, _) => SchedulePreview(zone);
        _filterSubscriptions[zone] = (filters, handler);
        filters.PropertyChanged += handler;
    }

    private void OnZonePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not Zone zone)
            return;

        if (e.PropertyName == nameof(Zone.Filters))
        {
            SubscribeFilters(zone);
            SchedulePreview(zone);
            return;
        }

        if (e.PropertyName is nameof(Zone.X) or nameof(Zone.Y) or nameof(Zone.Width) or nameof(Zone.Height) or nameof(Zone.Angle))
            SchedulePreview(zone);
    }

    private void SchedulePreview(Zone zone)
    {
        if (zone.Filters is null || !zone.Filters.HasAny)
        {
            CancelPreview(zone);
            ClearPreview(zone);
            return;
        }

        CancelPreview(zone);

        var cancellation = new CancellationTokenSource();
        _previewCancellations[zone] = cancellation;
        _ = RenderPreviewAsync(zone, cancellation);
    }

    private async Task RenderPreviewAsync(Zone zone, CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(200, cancellation.Token);

            var bitmap = await _previewRenderer.RenderAsync(zone, cancellation.Token);

            if (cancellation.IsCancellationRequested ||
                !_previewCancellations.TryGetValue(zone, out var current) ||
                !ReferenceEquals(current, cancellation) ||
                !Zones.Contains(zone))
            {
                DisposePreviewBitmap(bitmap);
                return;
            }

            var previous = zone.Preview;
            zone.Preview = bitmap;
            DisposePreviewBitmap(previous);
        }
        catch (OperationCanceledException)
        {
            // Превью устарело — это нормально.
        }
        catch (Exception ex)
        {
            StatusMessage = $"Не удалось построить превью зоны «{zone.Name}»: {ex.Message}";
        }
        finally
        {
            if (_previewCancellations.TryGetValue(zone, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _previewCancellations.Remove(zone);
            }

            cancellation.Dispose();
        }
    }

    private void CancelPreview(Zone zone)
    {
        if (_previewCancellations.Remove(zone, out var cancellation))
            cancellation.Cancel();
    }

    private void ClearPreview(Zone zone)
    {
        var preview = zone.Preview;
        zone.Preview = null;
        DisposePreviewBitmap(preview);
    }

    private async Task PreparePreviewSourceAsync(string path, byte[]? fallbackPng, int width, int height)
    {
        try
        {
            await _previewRenderer.PrepareAsync(path, fallbackPng, width, height);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Превью фильтров недоступно: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------------
    // Загрузка изображения
    // ---------------------------------------------------------------------

    /// <summary>Синхронная загрузка (используется для аргумента командной строки).</summary>
    private void LoadImage(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var bitmap = new Bitmap(fullPath);

        int width;
        int height;
        byte[]? fallbackPng = null;
        try
        {
            var info = Image.Identify(fullPath);
            width = info.Width;
            height = info.Height;
        }
        catch
        {
            width = bitmap.PixelSize.Width;
            height = bitmap.PixelSize.Height;
            fallbackPng = TryEncodeBitmapToPng(bitmap);
        }

        ApplyLoadedImage(fullPath, bitmap, width, height, fallbackPng);
        LoadZonesFromJson(fullPath);
    }

    /// <summary>Загрузка изображения из диалога выбора файла.</summary>
    public async Task LoadImageFromPathAsync(string path)
    {
        if (!File.Exists(path))
        {
            StatusMessage = $"Файл не найден: {path}";
            return;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);

            Bitmap bitmap;
            try
            {
                bitmap = await Task.Run(() => new Bitmap(fullPath));
            }
            catch
            {
                bitmap = new Bitmap(fullPath);
            }

            int width;
            int height;
            byte[]? fallbackPng = null;
            try
            {
                // Размеры читаем в фоне, чтобы не подвешивать интерфейс.
                var info = await Task.Run(() => Image.Identify(fullPath));
                width = info.Width;
                height = info.Height;
            }
            catch
            {
                // Формат не поддерживается ImageSharp — показываем через Avalonia
                // и запоминаем PNG-копию, чтобы экспорт тоже работал.
                width = bitmap.PixelSize.Width;
                height = bitmap.PixelSize.Height;
                fallbackPng = TryEncodeBitmapToPng(bitmap);
            }

            ApplyLoadedImage(fullPath, bitmap, width, height, fallbackPng);
            await LoadZonesFromJsonAsync(fullPath);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Не удалось открыть изображение: {ex.Message}";
        }
    }

    private void ApplyLoadedImage(string path, Bitmap bitmap, int width, int height, byte[]? fallbackPng)
    {
        _currentImagePath = path;
        _fallbackImagePng = fallbackPng;
        OriginalWidth = width;
        OriginalHeight = height;
        CurrentImage = bitmap;

        SelectedZone = null;
        Zones.Clear();

        _ = PreparePreviewSourceAsync(path, fallbackPng, width, height);

        HasImage = true;
        FitPending = true;
        WindowTitle = $"Редактор активных зон — {Path.GetFileName(path)}";
        StatusMessage = $"Изображение загружено: {width}×{height} px";

        UpdateScaledSize();
    }

    partial void OnCurrentImageChanged(Bitmap? oldValue, Bitmap? newValue)
    {
        // Старый Bitmap больше не нужен — освобождаем память.
        if (!ReferenceEquals(oldValue, newValue))
            oldValue?.Dispose();
    }

    // ---------------------------------------------------------------------
    // Масштаб
    // ---------------------------------------------------------------------

    partial void OnZoomLevelChanged(double value) => UpdateScaledSize();

    partial void OnOriginalWidthChanged(int value) => UpdateScaledSize();

    partial void OnOriginalHeightChanged(int value) => UpdateScaledSize();

    private void UpdateScaledSize()
    {
        ScaledImageWidth = OriginalWidth * ZoomLevel;
        ScaledImageHeight = OriginalHeight * ZoomLevel;
    }

    /// <summary>Вписывает изображение в видимую область с сохранением пропорций.</summary>
    public void FitToViewport(double viewportWidth, double viewportHeight)
    {
        if (!HasImage || OriginalWidth <= 0 || OriginalHeight <= 0)
            return;

        if (viewportWidth <= 1 || viewportHeight <= 1)
            return;

        var scale = Math.Min(viewportWidth / OriginalWidth, viewportHeight / OriginalHeight);
        SetZoom(scale);
        FitPending = false;
        StatusMessage = $"Масштаб: {ZoomLevel:P0}. Изображение вписано в окно.";
    }

    /// <summary>Изменение масштаба колесом мыши.</summary>
    public void Zoom(double delta)
    {
        if (!HasImage || Math.Abs(delta) < double.Epsilon)
            return;

        var factor = delta > 0 ? 1.15 : 1.0 / 1.15;
        SetZoom(ZoomLevel * factor);
    }

    public void SetZoom(double zoom)
    {
        if (!HasImage)
            return;

        ZoomLevel = Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    [RelayCommand(CanExecute = nameof(CanZoomIn))]
    private void ZoomIn() => SetZoom(ZoomLevel * 1.25);

    [RelayCommand(CanExecute = nameof(CanZoomOut))]
    private void ZoomOut() => SetZoom(ZoomLevel / 1.25);

    private bool CanZoomIn() => HasImage;

    private bool CanZoomOut() => HasImage;

    // ---------------------------------------------------------------------
    // Работа с зонами
    // ---------------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanEditZones))]
    private void AddZone()
    {
        if (OriginalWidth <= 0 || OriginalHeight <= 0)
            return;

        var width = Math.Max(MinZoneSize, Math.Round(OriginalWidth * 0.2));
        var height = Math.Max(MinZoneSize, Math.Round(OriginalHeight * 0.2));
        var x = Math.Max(0, (OriginalWidth - width) / 2.0);
        var y = Math.Max(0, (OriginalHeight - height) / 2.0);

        var zone = new Zone
        {
            Name = CreateZoneName(),
            X = x,
            Y = y,
            Width = width,
            Height = height,
            Angle = 0
        };

        Zones.Add(zone);
        SelectedZone = zone;
        StatusMessage = $"Добавлена зона «{zone.Name}».";
    }

    private string CreateZoneName()
    {
        var index = Zones.Count + 1;
        while (Zones.Any(z => z.Name == $"Зона {index}"))
            index++;

        return $"Зона {index}";
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedZone))]
    private void DeleteSelectedZone()
    {
        if (SelectedZone is null)
            return;

        var zone = SelectedZone;
        SelectedZone = null;
        Zones.Remove(zone);
        StatusMessage = $"Зона «{zone.Name}» удалена.";
    }

    private bool CanDeleteSelectedZone() => SelectedZone is not null;

    private bool CanEditZones() => HasImage;

    public void SelectZone(Zone? zone)
    {
        SelectedZone = zone;
    }

    partial void OnSelectedZoneChanged(Zone? oldValue, Zone? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;

        if (newValue is not null)
            newValue.IsSelected = true;

        HasSelectedZone = newValue is not null;
    }

    [RelayCommand(CanExecute = nameof(CanResetSelectedZoneFilters))]
    private void ResetSelectedZoneFilters()
    {
        var zone = SelectedZone;
        if (zone?.Filters is null)
            return;

        zone.Filters.Reset();
        StatusMessage = $"Фильтры зоны «{zone.Name}» сброшены.";
    }

    private bool CanResetSelectedZoneFilters() => SelectedZone is not null;

    // ---------------------------------------------------------------------
    // JSON
    // ---------------------------------------------------------------------

    public string? GetZonesJsonPath()
    {
        return _currentImagePath is null ? null : Path.ChangeExtension(_currentImagePath, ".json");
    }

    [RelayCommand(CanExecute = nameof(CanSaveZones))]
    private async Task SaveZonesAsync()
    {
        var jsonPath = GetZonesJsonPath();
        if (jsonPath is null)
            return;

        try
        {
            var json = JsonSerializer.Serialize(Zones.ToList(), JsonOptions);
            await File.WriteAllTextAsync(jsonPath, json);
            StatusMessage = $"Зоны сохранены: {jsonPath}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Не удалось сохранить JSON: {ex.Message}";
        }
    }

    private bool CanSaveZones() => HasImage;

    public Task LoadZonesFromJsonAsync(string imagePath)
    {
        var jsonPath = Path.ChangeExtension(imagePath, ".json");
        return LoadZonesFromJsonFileAsync(jsonPath, silentIfMissing: true);
    }

    /// <summary>Загружает зоны из произвольного JSON-файла (кнопка «Загрузить JSON»).</summary>
    public Task LoadZonesFromJsonFileAsync(string jsonPath)
    {
        return LoadZonesFromJsonFileAsync(jsonPath, silentIfMissing: false);
    }

    private async Task LoadZonesFromJsonFileAsync(string jsonPath, bool silentIfMissing)
    {
        if (!File.Exists(jsonPath))
        {
            if (!silentIfMissing)
                StatusMessage = $"JSON с зонами не найден ({Path.GetFileName(jsonPath)}).";
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(jsonPath);
            var loaded = JsonSerializer.Deserialize<List<Zone>>(json, JsonOptions);
            if (loaded is null)
                return;

            SelectedZone = null;
            Zones.Clear();
            foreach (var zone in loaded)
            {
                zone.IsSelected = false;
                Zones.Add(zone);
            }

            StatusMessage = $"Загружено зон из JSON: {Zones.Count}.";
        }
        catch (Exception ex)
        {
            SelectedZone = null;
            Zones.Clear();
            StatusMessage = $"Не удалось загрузить JSON с зонами: {ex.Message}";
        }
    }

    private void LoadZonesFromJson(string imagePath)
    {
        var jsonPath = Path.ChangeExtension(imagePath, ".json");
        if (!File.Exists(jsonPath))
        {
            StatusMessage = $"JSON с зонами не найден ({Path.GetFileName(jsonPath)}).";
            return;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            var loaded = JsonSerializer.Deserialize<List<Zone>>(json, JsonOptions);
            if (loaded is null)
                return;

            Zones.Clear();
            foreach (var zone in loaded)
            {
                zone.IsSelected = false;
                Zones.Add(zone);
            }

            StatusMessage = $"Загружено зон из JSON: {Zones.Count}.";
        }
        catch (Exception ex)
        {
            Zones.Clear();
            StatusMessage = $"Не удалось загрузить JSON с зонами: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------------
    // Экспорт
    // ---------------------------------------------------------------------

    /// <summary>Рендерит одну зону в PNG и возвращает байты изображения.</summary>
    public async Task<byte[]?> RenderZonePngAsync(Zone zone)
    {
        var path = _currentImagePath;
        if (path is null)
            return null;

        var fallbackPng = _fallbackImagePng;
        return await Task.Run(() =>
        {
            using var source = LoadSourceImage(path, fallbackPng);
            using var cropped = CropAndFilterZone(source, zone);
            return EncodePng(cropped);
        });
    }

    /// <summary>
    /// Рендерит зоны в PNG. Если зон несколько — они склеиваются вертикально
    /// в одно изображение (для копирования в буфер обмена).
    /// </summary>
    public async Task<byte[]?> RenderZonesPngAsync(IEnumerable<Zone>? zones = null)
    {
        var path = _currentImagePath;
        if (path is null)
            return null;

        var snapshot = (zones ?? Zones).ToArray();
        if (snapshot.Length == 0)
            return null;

        var fallbackPng = _fallbackImagePng;
        return await Task.Run(() => RenderZonesCore(path, fallbackPng, snapshot));
    }

    public async Task ExportZoneToFileAsync(Zone zone, string outputPath)
    {
        var bytes = await RenderZonePngAsync(zone);
        if (bytes is null)
            return;

        await File.WriteAllBytesAsync(outputPath, bytes);
        StatusMessage = $"Зона «{zone.Name}» экспортирована: {outputPath}";
    }

    public async Task ExportAllZonesToDirectoryAsync(string directory)
    {
        var path = _currentImagePath;
        var snapshot = Zones.ToArray();
        if (path is null || snapshot.Length == 0)
            return;

        var fallbackPng = _fallbackImagePng;
        try
        {
            await Task.Run(() =>
            {
                using var source = LoadSourceImage(path, fallbackPng);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var zone in snapshot)
                {
                    using var cropped = CropAndFilterZone(source, zone);

                    var baseName = SanitizeFileName(zone.Name);
                    if (baseName.Length == 0)
                        baseName = "zone";

                    var fileName = baseName;
                    var suffix = 2;
                    while (!usedNames.Add(fileName))
                        fileName = $"{baseName}_{suffix++}";

                    cropped.SaveAsPng(Path.Combine(directory, fileName + ".png"));
                }
            });

            StatusMessage = $"Экспортировано зон: {snapshot.Length} → {directory}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Не удалось экспортировать зоны: {ex.Message}";
        }
    }

    private static byte[] RenderZonesCore(string imagePath, byte[]? fallbackPng, IReadOnlyList<Zone> zones)
    {
        using var source = LoadSourceImage(imagePath, fallbackPng);

        var rendered = new List<Image>(zones.Count);
        try
        {
            foreach (var zone in zones)
                rendered.Add(CropAndFilterZone(source, zone));

            if (rendered.Count == 1)
                return EncodePng(rendered[0]);

            const int gap = 8;
            var width = Math.Max(1, rendered.Max(i => i.Width));
            var height = Math.Max(1, rendered.Sum(i => i.Height) + gap * (rendered.Count - 1));

            using var canvas = new Image<Rgba32>(width, height);
            var y = 0;
            foreach (var image in rendered)
            {
                var x = (width - image.Width) / 2;
                canvas.Mutate(ctx => ctx.DrawImage(image, new Point(x, y), 1f));
                y += image.Height + gap;
            }

            return EncodePng(canvas);
        }
        finally
        {
            foreach (var image in rendered)
                image.Dispose();
        }
    }

    private static byte[] EncodePng(Image image)
    {
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static Image<Rgba32> LoadSourceImage(string path, byte[]? fallbackPng)
    {
        return fallbackPng is { Length: > 0 }
            ? Image.Load<Rgba32>(fallbackPng)
            : Image.Load<Rgba32>(path);
    }

    /// <summary>Вырезает зону и применяет к ней фильтры (если они активны).</summary>
    private static Image<Rgba32> CropAndFilterZone(Image<Rgba32> source, Zone zone)
    {
        var cropped = ZoneCropper.Crop(source, zone);
        if (zone.Filters is null || !zone.Filters.HasAny)
            return cropped;

        var filtered = ZoneFilterProcessor.Apply(cropped, zone.Filters);
        cropped.Dispose();
        return filtered;
    }

    /// <summary>
    /// Кодирует Bitmap Avalonia в PNG — резервный путь для форматов,
    /// которые не поддерживает ImageSharp напрямую.
    /// </summary>
    private static byte[]? TryEncodeBitmapToPng(Bitmap bitmap)
    {
        try
        {
            using var stream = new MemoryStream();
            bitmap.Save(stream, new PngBitmapEncoderOptions());
            return stream.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Приводит угол к диапазону (-180; 180].</summary>
    public static double NormalizeAngle(double angle) => ZoneCropper.NormalizeAngle(angle);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().Trim('.');
    }

    /// <summary>
    /// Освобождает растровое превью после того, как UI успел отпустить его.
    /// </summary>
    private static void DisposePreviewBitmap(Bitmap? bitmap)
    {
        if (bitmap is null)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);
        else
            bitmap.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (var zone in _trackedZones.ToArray())
            UntrackZone(zone);

        _trackedZones.Clear();
        _previewRenderer.Dispose();
    }
}
