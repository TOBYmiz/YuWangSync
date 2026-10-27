namespace YuWangCloudSync.Core;

public static class ProductVersion
{
    public static string Current { get; } = typeof(ProductVersion).Assembly.GetName().Version!.ToString(4);
}
