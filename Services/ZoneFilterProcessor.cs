using System;
using System.Runtime.InteropServices;
using ImageZoneEditor.Models;
using OpenCvSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using CvSize = OpenCvSharp.Size;

namespace ImageZoneEditor.Services;

/// <summary>
/// Применяет фильтры и цветокоррекцию зоны к уже вырезанному изображению.
/// Работает на копии: исходное изображение и пиксели зоны не изменяются.
/// </summary>
public static class ZoneFilterProcessor
{
    /// <summary>
    /// Возвращает новое изображение с применёнными фильтрами.
    /// Если фильтры нейтральны, возвращается копия исходного.
    /// </summary>
    public static Image<Rgba32> Apply(Image<Rgba32> source, ZoneFilterSettings? settings)
    {
        if (settings is null || !settings.HasAny || source.Width < 1 || source.Height < 1)
            return source.Clone();

        var width = source.Width;
        var height = source.Height;

        var pixels = new byte[width * height * 4];
        source.CopyPixelDataTo(pixels);

        using var rgba = new Mat(height, width, MatType.CV_8UC4);
        Marshal.Copy(pixels, 0, rgba.Data, pixels.Length);

        // OpenCV работает с порядком BGRA.
        using var bgra = new Mat();
        Cv2.CvtColor(rgba, bgra, ColorConversionCodes.RGBA2BGRA);

        var channels = Cv2.Split(bgra);
        using var alpha = channels[3];
        using var bgr = new Mat();
        Cv2.Merge(new[] { channels[0], channels[1], channels[2] }, bgr);
        channels[0].Dispose();
        channels[1].Dispose();
        channels[2].Dispose();

        ApplyColorCorrection(bgr, settings);
        ApplyEffects(bgr, settings);
        ApplyBlurAndSharpen(bgr, settings);
        ApplyVignette(bgr, settings);

        var filteredChannels = Cv2.Split(bgr);
        using var outBgra = new Mat();
        try
        {
            Cv2.Merge(
                new[] { filteredChannels[0], filteredChannels[1], filteredChannels[2], alpha },
                outBgra);
        }
        finally
        {
            foreach (var channel in filteredChannels)
                channel.Dispose();
        }

        using var outRgba = new Mat();
        Cv2.CvtColor(outBgra, outRgba, ColorConversionCodes.BGRA2RGBA);

        var outPixels = new byte[width * height * 4];
        Marshal.Copy(outRgba.Data, outPixels, 0, outPixels.Length);

        return Image.LoadPixelData<Rgba32>(outPixels, width, height);
    }

    // ---------------------------------------------------------------------
    // Цвет
    // ---------------------------------------------------------------------

    private static void ApplyColorCorrection(Mat bgr, ZoneFilterSettings settings)
    {
        var contrast = Math.Clamp(1.0 + settings.Contrast / 100.0, 0.0, 4.0);
        var brightness = settings.Brightness * 1.0;

        if (Math.Abs(contrast - 1.0) > 0.0001 || Math.Abs(brightness) > 0.0001)
            ConvertInPlace(bgr, contrast, brightness);

        if (Math.Abs(settings.Gamma - 1.0) > 0.0001)
            ApplyGamma(bgr, settings.Gamma);

        if (Math.Abs(settings.Saturation) > 0.0001)
            ApplySaturation(bgr, settings.Saturation);

        if (Math.Abs(settings.Hue) > 0.0001)
            ApplyHue(bgr, settings.Hue);
    }

    private static void ConvertInPlace(Mat mat, double alpha, double beta)
    {
        using var tmp = new Mat();
        mat.ConvertTo(tmp, -1, alpha, beta);
        tmp.CopyTo(mat);
    }

    private static void ApplyGamma(Mat bgr, double gamma)
    {
        var safeGamma = Math.Clamp(gamma, 0.1, 10.0);
        var inverse = 1.0 / safeGamma;

        var lut = new byte[256];
        for (var i = 0; i < lut.Length; i++)
        {
            var value = Math.Pow(i / 255.0, inverse) * 255.0;
            lut[i] = (byte)Math.Clamp(Math.Round(value), 0, 255);
        }

        using var lutMat = new Mat(1, 256, MatType.CV_8UC1);
        Marshal.Copy(lut, 0, lutMat.Data, lut.Length);

        using var result = new Mat();
        Cv2.LUT(bgr, lutMat, result);
        result.CopyTo(bgr);
    }

    private static void ApplySaturation(Mat bgr, double saturation)
    {
        var factor = Math.Clamp(1.0 + saturation / 100.0, 0.0, 4.0);

        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        var channels = Cv2.Split(hsv);
        try
        {
            using var tmp = new Mat();
            channels[1].ConvertTo(tmp, -1, factor, 0);
            tmp.CopyTo(channels[1]);

            Cv2.Merge(channels, hsv);
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }

        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
    }

    private static void ApplyHue(Mat bgr, double hueDegrees)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

        var channels = Cv2.Split(hsv);
        try
        {
            // В OpenCV 8-битный канал H имеет диапазон 0…179 (1 единица = 2°).
            var shift = (int)Math.Round(hueDegrees / 2.0) % 180;
            if (shift < 0)
                shift += 180;

            // Cv2.LUT для 8-битных каналов требует таблицу ровно на 256 значений,
            // даже если реально используется только диапазон 0…179.
            var lut = new byte[256];
            for (var i = 0; i < lut.Length; i++)
                lut[i] = (byte)((i + shift) % 180);

            using var lutMat = new Mat(1, 256, MatType.CV_8UC1);
            Marshal.Copy(lut, 0, lutMat.Data, lut.Length);

            using var shifted = new Mat();
            Cv2.LUT(channels[0], lutMat, shifted);
            shifted.CopyTo(channels[0]);

            Cv2.Merge(channels, hsv);
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }

        Cv2.CvtColor(hsv, bgr, ColorConversionCodes.HSV2BGR);
    }

    // ---------------------------------------------------------------------
    // Эффекты
    // ---------------------------------------------------------------------

    private static void ApplyEffects(Mat bgr, ZoneFilterSettings settings)
    {
        if (settings.Grayscale)
        {
            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.CvtColor(gray, bgr, ColorConversionCodes.GRAY2BGR);
        }

        if (settings.Sepia)
            ApplySepia(bgr);

        if (settings.Invert)
            Cv2.BitwiseNot(bgr, bgr);
    }

    private static void ApplySepia(Mat bgr)
    {
        // Классическая матрица сепии. OpenCV хранит каналы в порядке BGR,
        // поэтому строки матрицы заданы для B, G и R соответственно.
        using var matrix = new Mat(3, 3, MatType.CV_32F);
        matrix.Set(0, 0, 0.131f);
        matrix.Set(0, 1, 0.534f);
        matrix.Set(0, 2, 0.272f);
        matrix.Set(1, 0, 0.168f);
        matrix.Set(1, 1, 0.686f);
        matrix.Set(1, 2, 0.349f);
        matrix.Set(2, 0, 0.189f);
        matrix.Set(2, 1, 0.769f);
        matrix.Set(2, 2, 0.393f);

        using var result = new Mat();
        Cv2.Transform(bgr, result, matrix);
        result.CopyTo(bgr);
    }

    private static void ApplyBlurAndSharpen(Mat bgr, ZoneFilterSettings settings)
    {
        if (settings.Blur > 0.2)
        {
            var kernel = (int)Math.Round(settings.Blur * 1.5);
            kernel = Math.Clamp(kernel | 1, 3, 31);
            Cv2.GaussianBlur(bgr, bgr, new CvSize(kernel, kernel), 0);
        }

        var sharpen = Math.Clamp(settings.Sharpen / 100.0, 0.0, 1.0) * 2.0;
        if (sharpen > 0.001)
        {
            using var blurred = new Mat();
            Cv2.GaussianBlur(bgr, blurred, new CvSize(0, 0), 1.2);
            Cv2.AddWeighted(bgr, 1.0 + sharpen, blurred, -sharpen, 0, bgr);
        }
    }

    private static void ApplyVignette(Mat bgr, ZoneFilterSettings settings)
    {
        var strength = Math.Clamp(settings.Vignette / 100.0, 0.0, 1.0);
        if (strength <= 0.001)
            return;

        var width = bgr.Width;
        var height = bgr.Height;
        if (width < 3 || height < 3)
            return;

        using var window = new Mat();
        Cv2.CreateHanningWindow(window, new CvSize(width, height), MatType.CV_32FC1);

        // mask = strength * hann + (1 - strength): в центре 1, по краям 1 - strength.
        using var mask = new Mat();
        window.ConvertTo(mask, MatType.CV_32FC1, strength, 1.0 - strength);

        var channels = Cv2.Split(bgr);
        try
        {
            foreach (var channel in channels)
            {
                using var floatChannel = new Mat();
                channel.ConvertTo(floatChannel, MatType.CV_32FC1);
                Cv2.Multiply(floatChannel, mask, floatChannel);
                floatChannel.ConvertTo(channel, MatType.CV_8UC1);
            }

            Cv2.Merge(channels, bgr);
        }
        finally
        {
            foreach (var channel in channels)
                channel.Dispose();
        }
    }
}
