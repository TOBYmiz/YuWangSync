namespace YuWangCloudSync.Tray;

internal readonly record struct SystemFileIconMetrics(int PixelSize, double SizeDip)
{
    internal const double LogicalSizeDip = 20;
    private const int MaximumPixelSize = 512;

    internal static SystemFileIconMetrics FromRasterizationScale(double rasterizationScale)
    {
        if (!double.IsFinite(rasterizationScale) || rasterizationScale <= 0)
            rasterizationScale = 1;

        var requestedPixels = Math.Round(LogicalSizeDip * rasterizationScale, MidpointRounding.AwayFromZero);
        var pixelSize = checked((int)Math.Clamp(requestedPixels, 1, MaximumPixelSize));
        return new(pixelSize, LogicalSizeDip);
    }
}
