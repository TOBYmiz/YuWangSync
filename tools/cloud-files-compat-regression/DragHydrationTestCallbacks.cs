namespace YuWangCloudSync.CloudFiles;

// The standalone provider in this harness does not report uncommitted HTTP bytes.
internal static class Callbacks
{
    internal static long GetInFlightHydrationBytes(long fileSystemId) => 0;
}
