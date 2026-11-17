using System;
using ImageZoneEditor.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageZoneEditor.Services;

/// <summary>
/// Вырезает зону из изображения. Общая логика для экспорта и для превью,
/// поэтому предпросмотр совпадает с тем, что попадёт в файл.
/// </summary>
public static class ZoneCropper
{
    /// <summary>
    /// Вырезает зону из исходного изображения. Если зона повёрнута,
    /// изображение предварительно поворачивается в обратную сторону,
    /// чтобы содержимое зоны получилось ровным.
    /// </summary>
    public static Image<Rgba32> Crop(Image<Rgba32> source, Zone zone)
    {
        var angle = NormalizeAngle(zone.Angle);
        var outWidth = Math.Max(1, (int)Math.Round(zone.Width));
        var outHeight = Math.Max(1, (int)Math.Round(zone.Height));

        var centerX = zone.X + zone.Width / 2.0;
        var centerY = zone.Y + zone.Height / 2.0;

        if (Math.Abs(angle) < 0.001)
        {
            var rect = ClampRect(
                new Rectangle((int)Math.Round(zone.X), (int)Math.Round(zone.Y), outWidth, outHeight),
                source.Width, source.Height);

            return source.Clone(ctx => ctx.Crop(rect));
        }

        var radians = angle * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var halfWidth = zone.Width / 2.0;
        var halfHeight = zone.Height / 2.0;

        var localCorners = new[]
        {
            (-halfWidth, -halfHeight),
            (halfWidth, -halfHeight),
            (halfWidth, halfHeight),
            (-halfWidth, halfHeight)
        };

        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        foreach (var (lx, ly) in localCorners)
        {
            var px = centerX + lx * cos - ly * sin;
            var py = centerY + lx * sin + ly * cos;
            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }

        var boundingRect = ClampRect(
            new Rectangle(
                (int)Math.Floor(minX) - 1,
                (int)Math.Floor(minY) - 1,
                (int)Math.Ceiling(maxX - minX) + 2,
                (int)Math.Ceiling(maxY - minY) + 2),
            source.Width, source.Height);

        using var bounding = source.Clone(ctx => ctx.Crop(boundingRect));
        bounding.Mutate(ctx => ctx.Rotate(-(float)angle, KnownResamplers.Bicubic));

        // Центр зоны после обратного поворота.
        var boundingCenterX = boundingRect.X + boundingRect.Width / 2.0;
        var boundingCenterY = boundingRect.Y + boundingRect.Height / 2.0;
        var offsetX = centerX - boundingCenterX;
        var offsetY = centerY - boundingCenterY;

        var rotatedCenterX = bounding.Width / 2.0 + (offsetX * cos + offsetY * sin);
        var rotatedCenterY = bounding.Height / 2.0 + (-offsetX * sin + offsetY * cos);

        var cropWidth = Math.Min(outWidth, bounding.Width);
        var cropHeight = Math.Min(outHeight, bounding.Height);

        var resultRect = ClampRect(
            new Rectangle(
                (int)Math.Round(rotatedCenterX - cropWidth / 2.0),
                (int)Math.Round(rotatedCenterY - cropHeight / 2.0),
                Math.Max(1, cropWidth),
                Math.Max(1, cropHeight)),
            bounding.Width, bounding.Height);

        return bounding.Clone(ctx => ctx.Crop(resultRect));
    }

    public static Rectangle ClampRect(Rectangle rect, int imageWidth, int imageHeight)
    {
        var x = Math.Clamp(rect.X, 0, Math.Max(0, imageWidth - 1));
        var y = Math.Clamp(rect.Y, 0, Math.Max(0, imageHeight - 1));
        var width = Math.Clamp(rect.Width, 1, imageWidth - x);
        var height = Math.Clamp(rect.Height, 1, imageHeight - y);
        return new Rectangle(x, y, width, height);
    }

    /// <summary>Приводит угол к диапазону (-180; 180].</summary>
    public static double NormalizeAngle(double angle)
    {
        angle %= 360.0;
        if (angle <= -180.0)
            angle += 360.0;
        else if (angle > 180.0)
            angle -= 360.0;

        return angle;
    }
}
