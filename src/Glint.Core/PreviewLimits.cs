namespace Glint.Core;

public static class PreviewLimits
{
    public const int ImageDimension = 2400;
    public static (int Width, int Height) Fit(double width, double height, int maxWidth = ImageDimension, int maxHeight = ImageDimension)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0 || maxWidth <= 0 || maxHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "文件页面尺寸无效。");
        var scale = Math.Min(1, Math.Min(maxWidth / width, maxHeight / height));
        return (Math.Clamp((int)Math.Round(width * scale), 1, maxWidth), Math.Clamp((int)Math.Round(height * scale), 1, maxHeight));
    }
}
