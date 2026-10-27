using System.Runtime.InteropServices;

namespace YuWangCloudSync.CloudFiles;

internal static class ExplorerStateNotifier
{
    private const uint ShcneUpdateItem = 0x00002000;
    private const uint ShcneUpdateDir = 0x00001000;
    private const uint ShcnfPathW = 0x0005;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Notification> Notifications = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Timer FlushTimer = new(Flush, null, 250, 250);

    private sealed class Notification
    {
        public uint EventId;
        public long LastSent;
        public long Due;
        public bool Pending;
    }

    public static void NotifyItem(string path) => Notify(path, ShcneUpdateItem);

    public static void NotifyDirectory(string path) => Notify(path, ShcneUpdateDir);

    private static void Notify(string path, uint eventId)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            lock (Gate)
            {
                if (!Notifications.TryGetValue(fullPath, out var notification))
                    Notifications[fullPath] = notification = new Notification();
                notification.EventId = eventId;
                if (!notification.Pending)
                    notification.Due = Math.Max(Environment.TickCount64 + 250, notification.LastSent + 2000);
                notification.Pending = true;
            }
        }
        catch { }
    }

    private static void Flush(object? state)
    {
        List<(string path, uint eventId)> ready = [];
        lock (Gate)
        {
            var now = Environment.TickCount64;
            foreach (var entry in Notifications)
            {
                if (!entry.Value.Pending || entry.Value.Due > now) continue;
                entry.Value.LastSent = now;
                entry.Value.Pending = false;
                ready.Add((entry.Key, entry.Value.EventId));
            }
            foreach (var entry in Notifications.Where(entry => !entry.Value.Pending
                         && now - entry.Value.LastSent > 30000).ToArray())
                Notifications.Remove(entry.Key);
        }
        foreach (var (path, eventId) in ready)
            SHChangeNotify(eventId, ShcnfPathW, path, IntPtr.Zero);
        GC.KeepAlive(FlushTimer);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
}
