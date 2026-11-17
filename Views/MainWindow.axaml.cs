using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ImageZoneEditor.Models;
using ImageZoneEditor.ViewModels;

namespace ImageZoneEditor.Views;

public partial class MainWindow : Window
{
    private const double MinZoneSize = 4.0;

    private enum DragMode
    {
        None,
        Move,
        ResizeNW,
        ResizeNE,
        ResizeSW,
        ResizeSE,
        Rotate
    }

    private DragMode _dragMode = DragMode.None;
    private Zone? _dragZone;
    private Point _startImagePoint;
    private double _startX;
    private double _startY;
    private double _startAngle;
    private double _startPointerAngle;
    private double _startAngleRadians;
    private Point _fixedCorner;
    private double _resizeSignX;
    private double _resizeSignY;

    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;

    // Держим Bitmap живым, пока он находится в системном буфере обмена.
    private Bitmap? _clipboardBitmap;

    public MainWindow()
    {
        InitializeComponent();

        // Подписываемся после InitializeComponent, иначе контролов ещё нет.
        ImageScrollViewer.PointerWheelChanged += OnPointerWheelChanged;
        ImageScrollViewer.SizeChanged += OnViewportSizeChanged;

        ImageScrollViewer.PointerPressed += OnScrollViewerPointerPressed;
        ImageScrollViewer.PointerMoved += OnScrollViewerPointerMoved;
        ImageScrollViewer.PointerReleased += OnScrollViewerPointerReleased;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FitToViewportIfNeeded(force: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (ViewModel is { } vm)
        {
            vm.Dispose();
            vm.CurrentImage?.Dispose();
        }

        _clipboardBitmap?.Dispose();
        base.OnClosed(e);
    }

    private void OnViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ViewModel is { FitPending: true })
            FitToViewportIfNeeded(force: false);
    }

    private void FitToViewportIfNeeded(bool force)
    {
        var vm = ViewModel;
        if (vm is null || !vm.HasImage)
            return;

        if (!force && !vm.FitPending)
            return;

        var viewport = ImageScrollViewer.Viewport;
        var width = viewport.Width;
        var height = viewport.Height;

        if (width <= 1 || height <= 1)
        {
            width = ImageScrollViewer.Bounds.Width - 4;
            height = ImageScrollViewer.Bounds.Height - 4;
        }

        vm.FitToViewport(width, height);
    }

    #region Масштаб

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is not { HasImage: true } vm)
            return;

        vm.Zoom(e.Delta.Y);
        e.Handled = true;
    }

    private void OnFitClick(object? sender, RoutedEventArgs e)
    {
        FitToViewportIfNeeded(force: true);
    }

    #endregion

    #region Панорамирование средней кнопкой мыши

    private void OnScrollViewerPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(ImageScrollViewer).Properties.IsMiddleButtonPressed)
            return;

        _isPanning = true;
        _panStart = e.GetPosition(ImageScrollViewer);
        _panStartOffset = ImageScrollViewer.Offset;
        e.Pointer.Capture(ImageScrollViewer);
        e.Handled = true;
    }

    private void OnScrollViewerPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning)
            return;

        var position = e.GetPosition(ImageScrollViewer);
        var delta = position - _panStart;

        ImageScrollViewer.Offset = new Vector(
            Math.Max(0, _panStartOffset.X - delta.X),
            Math.Max(0, _panStartOffset.Y - delta.Y));

        e.Handled = true;
    }

    private void OnScrollViewerPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning)
            return;

        _isPanning = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    #endregion

    #region Перемещение / ресайз / поворот зон

    /// <summary>
    /// Определяет зону, на которую кликнули, на какой её элемент и какое действие будет потом.
    /// </summary>
    private void OnImagePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.HasImage)
            return;

        // Проверяем, что клик был именно ЛКМ
        var point = e.GetCurrentPoint(ImageContainer); // ImageConteainer - генерит сам avalonia где-то у себя
        if (!point.Properties.IsLeftButtonPressed)
            return;

        // Определяем зону, на которую кликнули
        var source = e.Source as Control;
        var zone = FindZone(source);
        var handleName = FindHandleName(source);

        vm.SelectZone(zone);

        if (zone is null)
        {
            _dragMode = DragMode.None;
            vm.StatusMessage = "Зона не выбрана.";
            return;
        }

        vm.StatusMessage = $"Выбрана зона «{zone.Name}».";

        // Определяем чё потом будет делать перемещение курсора
        _dragMode = handleName switch
        {
            "ResizeHandleNW" => DragMode.ResizeNW,
            "ResizeHandleNE" => DragMode.ResizeNE,
            "ResizeHandleSW" => DragMode.ResizeSW,
            "ResizeHandleSE" => DragMode.ResizeSE,
            "RotateHandle" => DragMode.Rotate,
            _ => DragMode.Move
        };

        _dragZone = zone;
        _startImagePoint = ToImagePoint(e.GetPosition(ImageContainer), vm.ZoomLevel);
        _startX = zone.X;
        _startY = zone.Y;
        _startAngle = zone.Angle;
        _startAngleRadians = zone.Angle * Math.PI / 180.0;

        var center = new Point(zone.X + zone.Width / 2.0, zone.Y + zone.Height / 2.0);
        
        // Определяем чё потом будет делать перемещение курсора
        switch (_dragMode)
        {
            case DragMode.Rotate:
                _startPointerAngle = Math.Atan2(
                    _startImagePoint.Y - center.Y,
                    _startImagePoint.X - center.X) * 180.0 / Math.PI;
                break;

            case DragMode.ResizeNW:
                SetupResizeCorner(zone, -1, -1);
                break;
            case DragMode.ResizeNE:
                SetupResizeCorner(zone, 1, -1);
                break;
            case DragMode.ResizeSW:
                SetupResizeCorner(zone, -1, 1);
                break;
            case DragMode.ResizeSE:
                SetupResizeCorner(zone, 1, 1);
                break;
        }

        e.Pointer.Capture(ImageContainer);
        e.Handled = true;
    }

    private void SetupResizeCorner(Zone zone, double signX, double signY)
    {
        _resizeSignX = signX;
        _resizeSignY = signY;

        var center = new Point(zone.X + zone.Width / 2.0, zone.Y + zone.Height / 2.0);
        var radians = zone.Angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        // Неподвижный (противоположный) угол зоны в координатах изображения.
        var localX = -signX * zone.Width / 2.0;
        var localY = -signY * zone.Height / 2.0;

        _fixedCorner = new Point(
            center.X + localX * cos - localY * sin,
            center.Y + localX * sin + localY * cos);
    }

    private void OnImagePointerMoved(object? sender, PointerEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || _dragMode == DragMode.None || _dragZone is null)
            return;

        var position = ToImagePoint(e.GetPosition(ImageContainer), vm.ZoomLevel);
        var zone = _dragZone;

        switch (_dragMode)
        {
            case DragMode.Move:
            {
                var x = _startX + (position.X - _startImagePoint.X);
                var y = _startY + (position.Y - _startImagePoint.Y);

                var maxX = Math.Max(0, vm.OriginalWidth - zone.Width);
                var maxY = Math.Max(0, vm.OriginalHeight - zone.Height);
                zone.X = Math.Clamp(x, 0, maxX);
                zone.Y = Math.Clamp(y, 0, maxY);
                break;
            }

            case DragMode.ResizeNW:
            case DragMode.ResizeNE:
            case DragMode.ResizeSW:
            case DragMode.ResizeSE:
            {
                var cos = Math.Cos(_startAngleRadians);
                var sin = Math.Sin(_startAngleRadians);

                var deltaX = position.X - _fixedCorner.X;
                var deltaY = position.Y - _fixedCorner.Y;

                // Переводим вектор в локальные оси зоны (без учёта поворота).
                var localX = deltaX * cos + deltaY * sin;
                var localY = -deltaX * sin + deltaY * cos;

                var width = Math.Max(MinZoneSize, _resizeSignX * localX);
                var height = Math.Max(MinZoneSize, _resizeSignY * localY);

                // Новый центр относительно неподвижного угла.
                var centerOffsetX = _resizeSignX * width / 2.0;
                var centerOffsetY = _resizeSignY * height / 2.0;

                var centerX = _fixedCorner.X + centerOffsetX * cos - centerOffsetY * sin;
                var centerY = _fixedCorner.Y + centerOffsetX * sin + centerOffsetY * cos;

                zone.Width = width;
                zone.Height = height;
                zone.X = centerX - width / 2.0;
                zone.Y = centerY - height / 2.0;
                break;
            }

            case DragMode.Rotate:
            {
                var center = new Point(zone.X + zone.Width / 2.0, zone.Y + zone.Height / 2.0);
                var pointerAngle = Math.Atan2(
                    position.Y - center.Y,
                    position.X - center.X) * 180.0 / Math.PI;

                zone.Angle = MainWindowViewModel.NormalizeAngle(
                    _startAngle + (pointerAngle - _startPointerAngle));
                break;
            }
        }

        e.Handled = true;
    }

    private void OnImagePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragMode == DragMode.None)
            return;

        var vm = ViewModel;
        var zone = _dragZone;

        _dragMode = DragMode.None;
        _dragZone = null;
        e.Pointer.Capture(null);

        if (vm is not null && zone is not null)
        {
            vm.StatusMessage =
                $"Зона «{zone.Name}»: X={zone.X:F0}, Y={zone.Y:F0}, " +
                $"Ш={zone.Width:F0}, В={zone.Height:F0}, угол={zone.Angle:F1}°";
        }

        e.Handled = true;
    }

    private static Point ToImagePoint(Point screenPoint, double zoomLevel)
    {
        if (zoomLevel <= 0)
            return screenPoint;

        return new Point(screenPoint.X / zoomLevel, screenPoint.Y / zoomLevel);
    }

    private static Zone? FindZone(Control? control)
    {
        for (Visual? visual = control; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control { DataContext: Zone zone })
                return zone;
        }

        return null;
    }

    private static string? FindHandleName(Control? control)
    {
        for (Visual? visual = control; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is not Control current)
                continue;

            // Сначала проверяем имя: у маркеров DataContext тоже Zone,
            // поэтому проверка DataContext должна идти после имени.
            if (!string.IsNullOrEmpty(current.Name))
                return current.Name;

            if (current.DataContext is Zone)
                break;
        }

        return null;
    }

    #endregion

    #region Открытие и экспорт

    private async void OnOpenImageClick(object? sender, RoutedEventArgs e)
    {
        await OpenImageAsync();
    }

    private async Task OpenImageAsync()
    {
        var topLevel = GetTopLevel(this);
        if (topLevel is null)
            return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите изображение",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Изображения")
                    {
                        Patterns = new[]
                        {
                            "*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff",
                            "*.bmp", "*.gif", "*.webp", "*.heif", "*.heic"
                        }
                    }
                }
            });

            if (files.Count == 0 || ViewModel is not { } vm)
                return;

            await vm.LoadImageFromPathAsync(files[0].Path.LocalPath);
            FitToViewportIfNeeded(force: true);
        }
        catch (Exception ex)
        {
            if (ViewModel is { } vm)
                vm.StatusMessage = $"Не удалось открыть изображение: {ex.Message}";
        }
    }

    private async void OnLoadJsonClick(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.HasImage)
            return;

        var topLevel = GetTopLevel(this);
        if (topLevel is null)
            return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Выберите JSON с зонами",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("JSON с зонами")
                    {
                        Patterns = new[] { "*.json" }
                    }
                }
            });

            if (files.Count == 0)
                return;

            await vm.LoadZonesFromJsonFileAsync(files[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Не удалось загрузить JSON: {ex.Message}";
        }
    }

    private async void OnExportSelectedClick(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.HasImage)
            return;

        var zone = vm.SelectedZone;
        if (zone is null)
        {
            vm.StatusMessage = "Сначала выберите зону на изображении.";
            return;
        }

        var topLevel = GetTopLevel(this);
        if (topLevel is null)
            return;

        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Экспорт зоны",
                SuggestedFileName = MakeSafeFileName(zone.Name) + ".png",
                DefaultExtension = "png",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("PNG (без потерь)")
                    {
                        Patterns = new[] { "*.png" }
                    }
                }
            });

            if (file is null)
                return;

            await vm.ExportZoneToFileAsync(zone, file.Path.LocalPath);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Не удалось экспортировать зону: {ex.Message}";
        }
    }

    private async void OnExportAllClick(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.HasImage)
            return;

        if (vm.Zones.Count == 0)
        {
            vm.StatusMessage = "На изображении нет зон.";
            return;
        }

        var topLevel = GetTopLevel(this);
        if (topLevel is null)
            return;

        try
        {
            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Выберите папку для экспорта зон",
                AllowMultiple = false
            });

            if (folders.Count == 0)
                return;

            await vm.ExportAllZonesToDirectoryAsync(folders[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Не удалось экспортировать зоны: {ex.Message}";
        }
    }

    private async void OnCopyToClipboardClick(object? sender, RoutedEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null || !vm.HasImage)
            return;

        if (vm.Zones.Count == 0)
        {
            vm.StatusMessage = "На изображении нет зон.";
            return;
        }

        try
        {
            var zones = vm.SelectedZone is not null
                ? new[] { vm.SelectedZone }
                : null;

            var bytes = await vm.RenderZonesPngAsync(zones);
            if (bytes is null)
            {
                vm.StatusMessage = "Не удалось подготовить изображение для буфера обмена.";
                return;
            }

            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                bitmap.Dispose();
                vm.StatusMessage = "Буфер обмена недоступен.";
                return;
            }

            await clipboard.SetBitmapAsync(bitmap);

            var previousBitmap = _clipboardBitmap;
            _clipboardBitmap = bitmap;
            previousBitmap?.Dispose();
            vm.StatusMessage = vm.SelectedZone is not null
                ? $"Зона «{vm.SelectedZone.Name}» скопирована в буфер обмена."
                : $"Все зоны ({vm.Zones.Count}) скопированы в буфер обмена.";
        }
        catch (Exception ex)
        {
            vm.StatusMessage = $"Не удалось скопировать в буфер обмена: {ex.Message}";
        }
    }

    private static string MakeSafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        var result = new string(chars).Trim().Trim('.');
        return result.Length == 0 ? "zone" : result;
    }

    #endregion

    #region Горячие клавиши

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm is null)
            return;

        var control = e.KeyModifiers.HasFlag(KeyModifiers.Control);

        if (control && e.Key == Key.S)
        {
            if (vm.SaveZonesCommand.CanExecute(null))
                await vm.SaveZonesCommand.ExecuteAsync(null);

            e.Handled = true;
        }
        else if (control && e.Key == Key.O)
        {
            await OpenImageAsync();
            e.Handled = true;
        }
        else if (control && e.Key == Key.D)
        {
            if (vm.AddZoneCommand.CanExecute(null))
                vm.AddZoneCommand.Execute(null);

            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            if (vm.DeleteSelectedZoneCommand.CanExecute(null))
                vm.DeleteSelectedZoneCommand.Execute(null);

            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.SelectZone(null);
            e.Handled = true;
        }
    }
    #endregion
}
