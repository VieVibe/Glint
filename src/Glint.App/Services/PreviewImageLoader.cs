using System.Windows.Media.Imaging;
using Glint.Core;

namespace Glint.App.Services;

internal static class PreviewImageLoader
{
    public static BitmapImage Load(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var size = PreviewLimits.Fit(frame.PixelWidth, frame.PixelHeight);
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (frame.PixelWidth >= frame.PixelHeight) bitmap.DecodePixelWidth = size.Width;
        else bitmap.DecodePixelHeight = size.Height;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
