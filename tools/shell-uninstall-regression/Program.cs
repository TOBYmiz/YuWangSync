using System.Runtime.InteropServices;
using System.Reflection;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using YuWangCloudSync.CloudFiles;
using YuWangCloudSync.Core;

try { Run(args); }
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}

static void Run(string[] args)
{
    // Use a private application hive file, requiring no writes to the user's registry.
    // Optional native registration is redirected to that hive only in this process.
    var fixtureDirectory = Path.Combine(AppContext.BaseDirectory, "registry-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixtureDirectory);
    var fixturePath = Path.Combine(fixtureDirectory, "fixture.hiv");
    const string sid = "S-1-5-21-1-2-3-1000";
    var id = ProductIdentity.SyncRootRegistrationPrefix + sid + "!Default";
    var missingPath = Path.Combine(Path.GetTempPath(), "YuWang-removed-" + Guid.NewGuid().ToString("N"));
    var otherPath = missingPath + "-other";
    var loaded = RegLoadAppKeyW(fixturePath, out var fixtureHandle, 0xF003F, 1, 0);
    if (loaded != 0) throw new InvalidOperationException($"Create private hive failed: {loaded}");
    using var fixture = RegistryKey.FromHandle(new SafeRegistryHandle(fixtureHandle, ownsHandle: true));
    try
    {
        Check(!Directory.Exists(missingPath), "fixture target is absent");
        AddManager(id, sid, missingPath);
        var otherUserId = ProductIdentity.SyncRootRegistrationPrefix + sid + "1!Default";
        AddManager(otherUserId, sid + "1", otherPath);
        var foreignId = "AnotherProvider!" + sid + "!Default";
        AddManager(foreignId, sid, otherPath);
        var ownClass = AddNamespace(missingPath, ProductIdentity.ExecutableFileName);
        var otherClass = AddNamespace(otherPath, ProductIdentity.ExecutableFileName);
        var foreignClass = AddNamespace(missingPath, "AnotherProvider.exe");

        var roots = SyncRootShellRegistry.ReadRoots(fixture, sid, includeNamespaces: true);
        Check(roots.Any(root => root.Id == id && root.Path == missingPath), "missing folder registration discovered");
        Check(roots.All(root => root.Id == id), "other user and provider excluded");

        SyncRootShellRegistry.Remove(fixture, sid, otherUserId, otherPath, true);
        Check(Exists(SyncRootShellRegistry.ManagerPath + "\\" + otherUserId)
            && Exists(SyncRootShellRegistry.ClassesPath + "\\" + otherClass), "foreign SID removal refused");
        SyncRootShellRegistry.Remove(fixture, sid, id, otherPath + "-mismatch", true);
        Check(Exists(SyncRootShellRegistry.ManagerPath + "\\" + id), "manager path mismatch preserved");

        // A read-only key must fail, rather than turn a permission error into success.
        using (var readOnly = fixture.OpenSubKey("", writable: false)!)
        {
            var failed = false;
            try { SyncRootShellRegistry.Remove(readOnly, sid, id, missingPath, true); }
            catch (UnauthorizedAccessException) { failed = true; }
            catch (System.Security.SecurityException) { failed = true; }
            Check(failed, "write failure propagated");
        }

        SyncRootShellRegistry.Remove(fixture, sid, id, missingPath.ToUpperInvariant() + "\\", true);
        Check(!Exists(SyncRootShellRegistry.ManagerPath + "\\" + id), "missing folder manager registration removed");
        Check(!Exists(SyncRootShellRegistry.ClassesPath + "\\" + ownClass)
            && !Exists(SyncRootShellRegistry.ExplorerPath + @"\Desktop\NameSpace\" + ownClass)
            && !Exists(SyncRootShellRegistry.ExplorerPath + @"\MyComputer\NameSpace\" + ownClass),
            "generated CLSID and both namespace entries removed");
        using (var hidden = fixture.OpenSubKey(SyncRootShellRegistry.ExplorerPath + @"\HideDesktopIcons\NewStartPanel"))
            Check(hidden?.GetValue(ownClass) == null, "desktop visibility value removed");
        Check(Exists(SyncRootShellRegistry.ClassesPath + "\\" + foreignClass)
            && Exists(SyncRootShellRegistry.ClassesPath + "\\" + otherClass)
            && Exists(SyncRootShellRegistry.ManagerPath + "\\" + otherUserId)
            && Exists(SyncRootShellRegistry.ManagerPath + "\\" + foreignId), "unrelated registrations preserved");

        Check(SyncRootShellRegistry.ReadRoots(fixture, sid, true).Any(root => root.Path == otherPath),
            "orphan namespace discovered without SyncRootManager or configuration");
        Check(SyncRootShellRegistry.ReadRoots(fixture, sid, false).Length == 0,
            "machine view does not infer another user's namespaces");
        SyncRootShellRegistry.Remove(fixture, sid, id, otherPath, true);
        Check(!Exists(SyncRootShellRegistry.ClassesPath + "\\" + otherClass), "orphan namespace removed");
        SyncRootShellRegistry.Remove(fixture, sid, id, missingPath, true);
        Check(SyncRootShellRegistry.ReadRoots(fixture, sid, true).Length == 0, "cleanup is repeatable");

        TestUnregisterRoot();

        if (args is ["--shell-extension", var dllPath])
            TestNativeExtension(Path.GetFullPath(dllPath));
        Console.WriteLine("All shell uninstall regressions passed.");
    }
    finally
    {
        fixture.Dispose();
        Directory.Delete(fixtureDirectory, recursive: true);
    }

    void AddManager(string registrationId, string userSid, string path)
    {
        using var key = fixture.CreateSubKey(SyncRootShellRegistry.ManagerPath + "\\" + registrationId + @"\UserSyncRoots");
        key.SetValue(userSid, path);
    }

    string AddNamespace(string path, string executable)
    {
        var classId = Guid.NewGuid().ToString("B");
        var classPath = SyncRootShellRegistry.ClassesPath + "\\" + classId;
        using (var icon = fixture.CreateSubKey(classPath + @"\DefaultIcon"))
            icon.SetValue("", "\"C:\\Fixture App\\" + executable + "\",0");
        using (var properties = fixture.CreateSubKey(classPath + @"\Instance\InitPropertyBag"))
            properties.SetValue("TargetFolderPath", path);
        foreach (var location in new[] { @"\Desktop\NameSpace\", @"\MyComputer\NameSpace\" })
            fixture.CreateSubKey(SyncRootShellRegistry.ExplorerPath + location + classId).Dispose();
        using (var hidden = fixture.CreateSubKey(SyncRootShellRegistry.ExplorerPath + @"\HideDesktopIcons\NewStartPanel"))
            hidden.SetValue(classId, 1);
        return classId;
    }

    bool Exists(string path)
    {
        using var key = fixture.OpenSubKey(path);
        return key != null;
    }

    void TestUnregisterRoot()
    {
        var productionMethod = typeof(SyncRootRegistrar).Assembly.GetType("YuWangCloudSync.Program")!
            .GetMethod("UnregisterSyncRoot", BindingFlags.NonPublic | BindingFlags.Static)!;
        var userSid = WindowsIdentity.GetCurrent().User!.Value;
        var registrationId = ProductIdentity.SyncRootRegistrationPrefix + userSid + "!Regression";
        var ordinaryPath = Path.Combine(fixtureDirectory, "ordinary-directory");
        Directory.CreateDirectory(ordinaryPath);
        var markerPath = Path.Combine(ordinaryPath, "keep.txt");
        File.WriteAllText(markerPath, "Keep ordinary user content.");
        foreach (var path in new[] { missingPath, ordinaryPath })
        {
            AddManager(registrationId, userSid, path);
            var classId = AddNamespace(path, ProductIdentity.ExecutableFileName);
            RequireOverride(unchecked((int)0x80000002), fixture.Handle.DangerousGetHandle());
            try
            {
                RequireOverride(unchecked((int)0x80000001), fixture.Handle.DangerousGetHandle());
                try { productionMethod.Invoke(null, [path, new[] { registrationId }, true]); }
                finally { RequireOverride(unchecked((int)0x80000001), IntPtr.Zero); }
            }
            finally { RequireOverride(unchecked((int)0x80000002), IntPtr.Zero); }
            Check(!Exists(SyncRootShellRegistry.ManagerPath + "\\" + registrationId)
                && !Exists(SyncRootShellRegistry.ClassesPath + "\\" + classId),
                path == missingPath ? "production unregister removes absent folder integration"
                    : "production unregister removes integration after CFAPI registration is gone");
        }
        Check(File.ReadAllText(markerPath) == "Keep ordinary user content.", "ordinary directory contents preserved");
    }

    static void RequireOverride(int hive, IntPtr key)
    {
        var result = RegOverridePredefKey(new IntPtr(hive), key);
        if (result != 0) throw new InvalidOperationException($"Registry redirection failed: {result}");
    }

    void TestNativeExtension(string dllPath)
    {
        var library = NativeLibrary.Load(dllPath);
        try
        {
            var register = Marshal.GetDelegateForFunctionPointer<ServerEntry>(NativeLibrary.GetExport(library, "DllRegisterServer"));
            var unregister = Marshal.GetDelegateForFunctionPointer<ServerEntry>(NativeLibrary.GetExport(library, "DllUnregisterServer"));
            var result = RegOverridePredefKey(new IntPtr(unchecked((int)0x80000002)), fixture.Handle.DangerousGetHandle());
            if (result != 0) throw new InvalidOperationException($"Redirect HKLM failed: {result}");
            try
            {
                Check(register() == 0, "native extension registered in isolated hive");
                var overlay = SyncRootShellRegistry.ExplorerPath + @"\ShellIconOverlayIdentifiers\" + ProductIdentity.Identifier;
                const string classId = "{B7519EF2-1F39-4612-B5AB-FD70C37E905B}";
                var progId = @"Software\Classes\" + ProductIdentity.Identifier + ".SyncStatusOverlay.1";
                Check(Exists(overlay), "native overlay fixture present");
                Check(unregister() == 0, "native extension unregister returned success");
                Check(!Exists(overlay) && !Exists(SyncRootShellRegistry.ClassesPath + "\\" + classId)
                    && !Exists(progId), "native overlay, class and ProgID removed");
                using (var approved = fixture.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"))
                    Check(approved?.GetValue(classId) == null, "native approved entry removed");
                Check(unregister() == 0, "native unregister is repeatable");
            }
            finally
            {
                var restored = RegOverridePredefKey(new IntPtr(unchecked((int)0x80000002)), IntPtr.Zero);
                if (restored != 0) throw new InvalidOperationException($"Restore HKLM failed: {restored}");
            }
        }
        finally { NativeLibrary.Free(library); }
    }

    static void Check(bool passed, string name)
    {
        if (!passed) throw new InvalidOperationException("FAIL " + name);
        Console.WriteLine("PASS " + name);
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    static extern int RegOverridePredefKey(IntPtr key, IntPtr newKey);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int RegLoadAppKeyW(string file, out IntPtr key, int access, int options, int reserved);

}

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
delegate int ServerEntry();
