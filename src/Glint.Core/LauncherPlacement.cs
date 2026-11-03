namespace Glint.Core;

public readonly record struct DesktopBounds(int X, int Y, int Width, int Height);

public static class LauncherPlacement
{
    public static DesktopBounds Fit(DesktopBounds workArea, double scale)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(workArea));
        var margin = (int)Math.Round(10 * scale);
        var width = Math.Max(1, Math.Min((int)Math.Round(636 * scale), workArea.Width - margin * 2));
        var height = Math.Max(1, Math.Min((int)Math.Round(630 * scale), workArea.Height - margin * 2));
        return new(workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 3, width, height);
    }
}
