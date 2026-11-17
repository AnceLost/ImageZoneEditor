using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ImageZoneEditor.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageZoneEditor.Services;

/// <summary>
/// Готовит уменьшенные превью зон с фильтрами. Хранит уменьшенную копию
/// исходного изображения и не трогает оригинальный файл.
/// </summary>
public sealed class ZonePreviewRenderer : IDisposable
{
    private const int SourceMaxSide = 1600;
    private const int PreviewMaxSide = 640;

    private readonly object _gate = new();
    private Image<Rgba32>? _source;
    private double _scale = 1.0;
    private int _generation;
    private Task _prepareTask = Task.CompletedTask;

    /// <summary>
    /// Загружает исходное изображение и уменьшает его для быстрых превью.
    /// Предыдущая копия освобождается.
    /// </summary>
    public Task PrepareAsync(string path, byte[]? fallbackPng, int originalWidth, int originalHeight)
    {
        var generation = Interlocked.Increment(ref _generation);

        _prepareTask = Task.Run(() =>
        {
            var image = fallbackPng is { Length: > 0 }
                ? Image.Load<Rgba32>(fallbackPng)
                : Image.Load<Rgba32>(path);

            if (image.Width > SourceMaxSide || image.Height > SourceMaxSide)
            {
                image.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(SourceMaxSide, SourceMaxSide),
                    Mode = ResizeMode.Max
                }));
            }

            lock (_gate)
            {
                if (generation != _generation)
                {
                    image.Dispose();
                    return;
                }

                _source?.Dispose();
                _source = image;
                _scale = originalWidth > 0
                    ? (double)image.Width / originalWidth
                    : 1.0;
            }
        });

        return _prepareTask;
    }

    /// <summary>
    /// Рендерит превью зоны с фильтрами. Возвращает null, если зона
    /// не попадает в изображение.
    /// </summary>
    public async Task<Bitmap?> RenderAsync(Zone zone, CancellationToken cancellationToken)
    {
        await _prepareTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        Image<Rgba32>? crop = null;

        lock (_gate)
        {
            var source = _source;
            if (source is null)
                return null;

            var scale = _scale;
            var scaled = new Zone
            {
                X = zone.X * scale,
                Y = zone.Y * scale,
                Width = Math.Max(1.0, zone.Width * scale),
                Height = Math.Max(1.0, zone.Height * scale),
                Angle = zone.Angle
            };

            crop = ZoneCropper.Crop(source, scaled);
        }

        try
        {
            if (crop.Width > PreviewMaxSide || crop.Height > PreviewMaxSide)
            {
                crop.Mutate(context => context.Resize(new ResizeOptions
                {
                    Size = new Size(PreviewMaxSide, PreviewMaxSide),
                    Mode = ResizeMode.Max
                }));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var filtered = ZoneFilterProcessor.Apply(crop, zone.Filters);
            try
            {
                using var stream = new MemoryStream();
                filtered.SaveAsPng(stream);
                stream.Position = 0;
                return new Bitmap(stream);
            }
            finally
            {
                filtered.Dispose();
            }
        }
        finally
        {
            crop.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Interlocked.Increment(ref _generation);
            _source?.Dispose();
            _source = null;
        }
    }
}
