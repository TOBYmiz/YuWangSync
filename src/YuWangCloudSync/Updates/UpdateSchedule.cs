namespace YuWangCloudSync.Updates;

internal static class UpdateSchedule
{
    internal static bool IsDue(DateOnly? lastSuccessfulCheckDate, DateOnly localDate) =>
        lastSuccessfulCheckDate != localDate;

    internal static bool IsNewerVersion(Version latest, string currentVersion) =>
        Version.TryParse(currentVersion, out var current) && latest > current;

    internal static TimeSpan UntilNextLocalDay(DateTime localNow)
    {
        var delay = localNow.Date.AddDays(1) - localNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromSeconds(1);
    }
}
