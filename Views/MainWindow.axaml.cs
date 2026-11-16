using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImageZoneEditor.ViewModels;
using System.Linq;

namespace ImageZoneEditor.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        // Обработка колесика мыши
        if (ImageScrollViewer != null)
        {
            ImageScrollViewer.PointerWheelChanged += OnPointerWheelChanged;
        }
    }

    // Логика зума
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.Zoom(e.Delta.Y);
            e.Handled = true;
        }
    }

    // Логика открытия файла (вешаем на кнопку "Открыть" в XAML)
    private async void OnOpenImageClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel == null) return;

        // Открываем диалог выбора файла
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Выберите изображение",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Изображения")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.tiff", "*.tif", "*.heif" }
                }
            }
        });

        if (files.Count >= 1)
        {
            var file = files[0];
            if (DataContext is MainWindowViewModel vm)
            {
                // Передаем путь к файлу во ViewModel
                await vm.LoadImageFromPathAsync(file.Path.LocalPath);
            }
        }
    }
}