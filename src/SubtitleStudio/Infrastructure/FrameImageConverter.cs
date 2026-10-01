using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SubtitleStudio.Services.Video;

namespace SubtitleStudio.Infrastructure;

/// <summary>FrameSample (raw BGRA from ffmpeg) to a frozen WPF bitmap. Keeps WPF imaging out of view models.</summary>
public sealed class FrameImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FrameSample frame) return null;
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null, frame.Bgra, frame.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
