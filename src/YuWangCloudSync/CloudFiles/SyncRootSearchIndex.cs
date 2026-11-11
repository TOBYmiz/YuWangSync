using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using YuWangCloudSync.Core;

namespace YuWangCloudSync.CloudFiles;

/// <summary>
/// The Shell caches volatile transfer properties in Windows Search. Roots outside
/// its scope accept SetValue/Commit but may never expose progress to Explorer.
/// Use default rules so user exclusions keep priority, and journal only our additions.
/// </summary>
internal static class SyncRootSearchIndex
{
    private const string MarkerKey = @"Software\" + ProductIdentity.Identifier + @"\SearchIndexScopes";

    // Search's crawl scope API requires a fully URL-decoded file URL, unlike
    // ordinary web URLs. Escaped spaces/#/% can register a different, nonexistent path.
    internal static string FolderUrl(string path) =>
        "file:///" + Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static string MarkerName(string url) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.ToUpperInvariant())));

    public static bool Ensure(string path, Action<string>? log)
    {
        try
        {
            var url = FolderUrl(path);
            using var scope = new SearchScope();
            var (included, reason) = scope.Contains(url);
            if (included)
            {
                log?.Invoke($"[UPLOAD] 同步根已在搜索索引范围内 path={path}");
                return true;
            }
            // Unknown scope is the missing registration case. Preserve explicit
            // default/user/group-policy/usage exclusions rather than replacing them.
            if (reason != 0)
            {
                log?.Invoke($"[UPLOAD] 同步根被搜索索引规则排除，Explorer 进度可能不可见 reason={reason} path={path}");
                return false;
            }
            // Persist ownership before the COM mutation, including a failed SaveAll
            // or a crash. Never remove a pre-existing or inherited inclusion rule.
            using var marker = Registry.CurrentUser.CreateSubKey(MarkerKey);
            marker.SetValue(MarkerName(url), url);
            marker.Flush();
            scope.Add(url);
            var usable = scope.Contains(url).included;
            log?.Invoke($"[UPLOAD] 同步根搜索索引注册完成 included={usable} path={path}");
            return usable;
        }
        catch (Exception error)
        {
            log?.Invoke($"[UPLOAD] 同步根搜索索引不可用，Explorer 进度可能不可见 hr=0x{error.HResult:X8} path={path}");
            return false;
        }
    }

    public static bool Remove(string path, Action<string>? log)
    {
        try
        {
            var url = FolderUrl(path);
            using var marker = Registry.CurrentUser.OpenSubKey(MarkerKey, writable: true);
            var name = MarkerName(url);
            if (!string.Equals(marker?.GetValue(name) as string, url, StringComparison.OrdinalIgnoreCase)) return true;
            using var scope = new SearchScope();
            scope.Remove(url);
            marker!.DeleteValue(name, throwOnMissingValue: false);
            log?.Invoke($"[UPLOAD] 已移除本程序添加的同步根搜索索引规则 path={path}");
            return true;
        }
        catch (Exception error)
        {
            log?.Invoke($"[UPLOAD] 移除同步根搜索索引规则失败，保留记录供重试 hr=0x{error.HResult:X8} path={path}");
            return false;
        }
    }

    // Fixed SDK vtable slots and typed delegates work in NativeAOT. In particular,
    // GetCatalog is slot 10 (after SetProxy), and GetCrawlScopeManager is slot 28.
    internal sealed class SearchScope : IDisposable
    {
        private IntPtr _manager, _catalog, _scope;
        private bool _uninitialize;

        public SearchScope()
        {
            var initialized = CoInitializeEx(IntPtr.Zero, 0);
            _uninitialize = initialized >= 0;
            if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
            try
            {
                Marshal.ThrowExceptionForHR(CoCreateInstance(new("7D096C5F-AC08-4F1F-BEB7-5C22C517CE39"),
                    IntPtr.Zero, 4, new("AB310581-AC80-11D1-8DF3-00C04FB6EF69"), out _manager));
                Marshal.ThrowExceptionForHR(Method<GetCatalog>(_manager, 10)(_manager, "SystemIndex", out _catalog));
                Marshal.ThrowExceptionForHR(Method<GetScope>(_catalog, 28)(_catalog, out _scope));
            }
            catch { Dispose(); throw; }
        }

        public (bool included, int reason) Contains(string url)
        {
            Marshal.ThrowExceptionForHR(Method<Included>(_scope, 14)(_scope, url, out var included, out var reason));
            return (included != 0, reason);
        }

        public void Add(string url, bool include = true)
        {
            Marshal.ThrowExceptionForHR(Method<AddRule>(_scope, 3)(_scope, url, include ? 1 : 0, 1));
            Marshal.ThrowExceptionForHR(Method<Save>(_scope, 16)(_scope));
        }

        public void Remove(string url)
        {
            var result = Method<RemoveRule>(_scope, 18)(_scope, url);
            // A prior cleanup may have removed the rule before the ownership marker.
            if (result != unchecked((int)0x80070002)) Marshal.ThrowExceptionForHR(result);
            Marshal.ThrowExceptionForHR(Method<Save>(_scope, 16)(_scope));
        }

        public void Dispose()
        {
            if (_scope != IntPtr.Zero) { Method<Release>(_scope, 2)(_scope); _scope = IntPtr.Zero; }
            if (_catalog != IntPtr.Zero) { Method<Release>(_catalog, 2)(_catalog); _catalog = IntPtr.Zero; }
            if (_manager != IntPtr.Zero) { Method<Release>(_manager, 2)(_manager); _manager = IntPtr.Zero; }
            if (_uninitialize) { CoUninitialize(); _uninitialize = false; }
        }

        private static T Method<T>(IntPtr instance, int slot) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int GetCatalog(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr catalog);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetScope(IntPtr self, out IntPtr scope);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int Included(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string url, out int included, out int reason);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int AddRule(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string url, int include, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int RemoveRule(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string url);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Save(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint Release(IntPtr self);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoInitializeEx(IntPtr reserved, uint mode);
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern void CoUninitialize();
        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);
    }
}
