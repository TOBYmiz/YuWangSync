using System.ComponentModel;
using System.Runtime.InteropServices;

namespace YuWangCloudSync.Tray;

internal readonly record struct SystemFileAssociationInfo(string TypeName, byte[]? IconPixels, int? IconSourcePixelSize = null);

/// <summary>Reads the current Windows Shell association for a file extension.</summary>
internal static class SystemFileAssociation
{
    internal const int DefaultIconSize = 32;
    private const int MaximumIconSize = 512;
    private static readonly object ShellAssociationLock = new();

    private const int DisplayNameLength = 260;
    private const int TypeNameLength = 80;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiTypeName = 0x00000400;
    private const uint ShgfiSysIconIndex = 0x00004000;
    private const uint ShgfiUseFileAttributes = 0x00000010;
    private const uint DiNormal = 0x0003;
    private const uint DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const uint AssocStrFriendlyDocName = 3;
    private const uint CoInitMultiThreaded = 0;

    private const int ShilSmall = 1;
    private const int ShilLarge = 0;
    private const int ShilExtraLarge = 2;
    private const int ShilJumbo = 4;
    private const int ImageListGetIconSlot = 10;
    private const int ImageListGetIconSizeSlot = 16;
    private const uint IldTransparent = 0x00000001;
    private static readonly Guid ImageListInterfaceId = new("46EB5926-582E-4017-9FDF-E8998DAA0950");

    internal static Task<SystemFileAssociationInfo> QueryAsync(string extension, int iconPixelSize = DefaultIconSize,
        bool includeIcon = true)
    {
        if (iconPixelSize is < 1 or > MaximumIconSize)
            throw new ArgumentOutOfRangeException(nameof(iconPixelSize));
        return Task.Run(() => Query(extension, iconPixelSize, includeIcon));
    }

    internal static Task<byte[]?> QueryDirectoryIconAsync(string path, int iconPixelSize = DefaultIconSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (iconPixelSize is < 1 or > MaximumIconSize)
            throw new ArgumentOutOfRangeException(nameof(iconPixelSize));
        return Task.Run(() => QueryDirectoryIcon(path, iconPixelSize));
    }

    private static unsafe byte[]? QueryDirectoryIcon(string path, int iconPixelSize)
    {
        var initializationResult = CoInitializeEx(IntPtr.Zero, CoInitMultiThreaded);
        if (initializationResult < 0)
            Marshal.ThrowExceptionForHR(initializationResult);

        try
        {
            lock (ShellAssociationLock)
            {
                var shellInfo = new ShellFileInfo();
                var result = SHGetFileInfoW(path, FileAttributeDirectory, ref shellInfo,
                    (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon);
                if (result == 0 || shellInfo.Icon == IntPtr.Zero)
                {
                    Serilog.Log.Warning("SHGetFileInfoW 未返回文件夹图标：{Path}，LastError={LastError}",
                        path, Marshal.GetLastWin32Error());
                    return null;
                }

                try
                {
                    return RenderIconPixels(shellInfo.Icon, iconPixelSize);
                }
                catch (Exception error)
                {
                    Serilog.Log.Warning(error, "渲染系统文件夹图标失败：{Path}", path);
                    return null;
                }
                finally
                {
                    _ = DestroyIcon(shellInfo.Icon);
                }
            }
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static SystemFileAssociationInfo Query(string extension, int iconPixelSize, bool includeIcon)
    {
        var initializationResult = CoInitializeEx(IntPtr.Zero, CoInitMultiThreaded);
        if (initializationResult < 0)
            Marshal.ThrowExceptionForHR(initializationResult);

        try
        {
            // Serialize the Shell lookups and icon extraction across DPI-sized requests.
            lock (ShellAssociationLock)
                return QueryShellAssociation(extension, includeIcon ? iconPixelSize : null);
        }
        finally
        {
            CoUninitialize();
        }
    }

    private static unsafe SystemFileAssociationInfo QueryShellAssociation(string extension, int? iconPixelSize)
    {
        var shellInfo = new ShellFileInfo();
        var flags = ShgfiTypeName | ShgfiUseFileAttributes | (iconPixelSize.HasValue ? ShgfiSysIconIndex : 0);
        var result = SHGetFileInfoW(extension, FileAttributeNormal, ref shellInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(), flags);
        if (result == 0)
            Serilog.Log.Warning("SHGetFileInfoW 未返回关联信息：{Extension}，LastError={LastError}",
                extension, Marshal.GetLastWin32Error());

        var typeName = QueryFriendlyDocumentName(extension);
        if (string.IsNullOrWhiteSpace(typeName))
            typeName = ReadTypeName(shellInfo);
        if (iconPixelSize is not int targetSize) return new(typeName, null);

        if (result != 0)
        {
            try
            {
                if (RenderImageListIcon(shellInfo.IconIndex, targetSize) is { } icon)
                    return new(typeName, icon.Pixels, icon.SourcePixelSize);
            }
            catch (Exception error)
            {
                Serilog.Log.Debug(error, "读取较大的系统文件类型图标失败：{Extension}", extension);
            }
        }

        // Keep the original HICON path as a last resort if the system image lists are unavailable.
        shellInfo = new ShellFileInfo();
        result = SHGetFileInfoW(extension, FileAttributeNormal, ref shellInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(), ShgfiIcon | ShgfiUseFileAttributes);
        try
        {
            return new(typeName, result != 0 && shellInfo.Icon != IntPtr.Zero
                ? RenderIconPixels(shellInfo.Icon, targetSize)
                : null);
        }
        catch (Exception error)
        {
            Serilog.Log.Warning(error, "渲染系统文件类型图标失败：{Extension}", extension);
            return new(typeName, null);
        }
        finally
        {
            if (shellInfo.Icon != IntPtr.Zero)
                _ = DestroyIcon(shellInfo.Icon);
        }
    }

    private static unsafe (byte[] Pixels, int SourcePixelSize)? RenderImageListIcon(int iconIndex, int targetSize)
    {
        ReadOnlySpan<int> tiers = [ShilSmall, ShilLarge, ShilExtraLarge, ShilJumbo];
        var fallbackTier = -1;
        var fallbackSize = 0;
        for (var i = 0; i <= tiers.Length; i++)
        {
            var useFallback = i == tiers.Length;
            var tier = useFallback ? fallbackTier : tiers[i];
            if (tier < 0) break;
            if (SHGetImageList(tier, in ImageListInterfaceId, out var imageList) < 0 || imageList == IntPtr.Zero)
                continue;

            try
            {
                // Use the actual list dimensions: the non-jumbo tiers can be DPI-scaled by Shell.
                var vtable = *(nint**)imageList;
                var getIconSize = (delegate* unmanaged[Stdcall]<nint, int*, int*, int>)vtable[ImageListGetIconSizeSlot];
                int width, height;
                if (getIconSize(imageList, &width, &height) < 0 || width != height || width is < 1 or > MaximumIconSize)
                    continue;

                if (!useFallback && tier != ShilJumbo && width <= targetSize)
                {
                    fallbackTier = tier;
                    fallbackSize = width;
                    continue;
                }

                var getIcon = (delegate* unmanaged[Stdcall]<nint, int, uint, nint*, int>)vtable[ImageListGetIconSlot];
                nint icon = 0;
                try
                {
                    if (getIcon(imageList, iconIndex, IldTransparent, &icon) < 0 || icon == 0) continue;
                    // Jumbo sometimes contains only an extra-large icon in its top-left corner.
                    // Use the smaller list instead of shrinking the icon together with that padding.
                    var checkJumboPadding = tier == ShilJumbo && fallbackSize > 0 && fallbackSize * 2 <= width;
                    if (width <= targetSize && !checkJumboPadding)
                        return (RenderIconPixels(icon, targetSize), width);

                    var pixels = RenderIconPixels(icon, width);
                    if (checkJumboPadding && !HasPixelsOutsideTopLeft(pixels, width, fallbackSize))
                        continue;
                    if (width < targetSize)
                        return (RenderIconPixels(icon, targetSize), width);
                    return (width == targetSize ? pixels : DownsampleIconPixels(pixels, width, targetSize), width);
                }
                finally
                {
                    if (icon != 0) _ = DestroyIcon(icon);
                }
            }
            finally
            {
                _ = Marshal.Release(imageList);
            }
        }

        return null;
    }

    private static bool HasPixelsOutsideTopLeft(byte[] pixels, int imageSize, int contentSize)
    {
        for (var y = 0; y < imageSize; y++)
        {
            for (var x = y < contentSize ? contentSize : 0; x < imageSize; x++)
            {
                if (pixels[(y * imageSize + x) * 4 + 3] != 0) return true;
            }
        }
        return false;
    }

    // Area averaging in premultiplied BGRA preserves soft edges, including non-integer scale ratios.
    internal static byte[] DownsampleIconPixels(byte[] pixels, int sourceSize, int targetSize)
    {
        var output = new byte[targetSize * targetSize * 4];
        // On the shared integer grid, each destination pixel spans sourceSize units per axis
        // and each source pixel spans targetSize units. Intersections give exact weights.
        var area = sourceSize * sourceSize;
        for (var y = 0; y < targetSize; y++)
        {
            var top = y * sourceSize;
            var bottom = top + sourceSize;
            for (var x = 0; x < targetSize; x++)
            {
                var left = x * sourceSize;
                var right = left + sourceSize;
                int blue = 0, green = 0, red = 0, alpha = 0;
                for (var sy = top / targetSize; sy <= (bottom - 1) / targetSize; sy++)
                {
                    var height = Math.Min(bottom, (sy + 1) * targetSize) - Math.Max(top, sy * targetSize);
                    for (var sx = left / targetSize; sx <= (right - 1) / targetSize; sx++)
                    {
                        var width = Math.Min(right, (sx + 1) * targetSize) - Math.Max(left, sx * targetSize);
                        var weight = width * height;
                        var offset = (sy * sourceSize + sx) * 4;
                        blue += pixels[offset] * weight;
                        green += pixels[offset + 1] * weight;
                        red += pixels[offset + 2] * weight;
                        alpha += pixels[offset + 3] * weight;
                    }
                }

                var destination = (y * targetSize + x) * 4;
                output[destination] = (byte)((blue + area / 2) / area);
                output[destination + 1] = (byte)((green + area / 2) / area);
                output[destination + 2] = (byte)((red + area / 2) / area);
                output[destination + 3] = (byte)((alpha + area / 2) / area);
            }
        }

        return output;
    }

    private static unsafe string QueryFriendlyDocumentName(string extension)
    {
        try
        {
            uint characterCount = 0;
            _ = AssocQueryStringW(0, AssocStrFriendlyDocName, extension, IntPtr.Zero, IntPtr.Zero,
                ref characterCount);
            if (characterCount is < 2 or > 4096) return "";

            var buffer = new char[checked((int)characterCount)];
            fixed (char* output = buffer)
            {
                var capacity = characterCount;
                var result = AssocQueryStringW(0, AssocStrFriendlyDocName, extension, IntPtr.Zero,
                    (IntPtr)output, ref capacity);
                if (result < 0) return "";

                var length = 0;
                while (length < buffer.Length && output[length] != '\0') length++;
                return new string(output, 0, length).Trim();
            }
        }
        catch
        {
            return "";
        }
    }

    private static unsafe string ReadTypeName(ShellFileInfo shellInfo)
    {
        char* typeName = shellInfo.TypeName;
        var length = 0;
        while (length < TypeNameLength && typeName[length] != '\0') length++;
        return new string(typeName, 0, length).Trim();
    }

    private static byte[] RenderIconPixels(IntPtr icon, int iconPixelSize)
    {
        var deviceContext = CreateCompatibleDC(IntPtr.Zero);
        if (deviceContext == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());

        IntPtr bitmap = IntPtr.Zero;
        IntPtr previousObject = IntPtr.Zero;
        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = iconPixelSize,
                    Height = -iconPixelSize,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)(iconPixelSize * iconPixelSize * 4),
                },
            };
            bitmap = CreateDIBSection(deviceContext, ref info, DibRgbColors,
                out var pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());

            previousObject = SelectObject(deviceContext, bitmap);
            if (previousObject == IntPtr.Zero || previousObject == new IntPtr(-1))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var byteCount = iconPixelSize * iconPixelSize * 4;
            var background = new byte[byteCount];
            Marshal.Copy(background, 0, pixels, byteCount);
            DrawIcon(deviceContext, icon, iconPixelSize);
            _ = GdiFlush();
            var blackBackground = new byte[byteCount];
            Marshal.Copy(pixels, blackBackground, 0, byteCount);

            Array.Fill(background, (byte)255);
            Marshal.Copy(background, 0, pixels, byteCount);
            DrawIcon(deviceContext, icon, iconPixelSize);
            _ = GdiFlush();
            var whiteBackground = new byte[byteCount];
            Marshal.Copy(pixels, whiteBackground, 0, byteCount);

            // Rendering against black and white recovers premultiplied alpha for both modern
            // ARGB icons and legacy mask-based icons returned by the Shell.
            for (var offset = 0; offset < byteCount; offset += 4)
            {
                var blueDifference = whiteBackground[offset] - blackBackground[offset];
                var greenDifference = whiteBackground[offset + 1] - blackBackground[offset + 1];
                var redDifference = whiteBackground[offset + 2] - blackBackground[offset + 2];
                var transparency = Math.Clamp((blueDifference + greenDifference + redDifference + 1) / 3, 0, 255);
                blackBackground[offset + 3] = (byte)(255 - transparency);
            }

            return blackBackground;
        }
        finally
        {
            if (previousObject != IntPtr.Zero && previousObject != new IntPtr(-1))
                _ = SelectObject(deviceContext, previousObject);
            if (bitmap != IntPtr.Zero)
                _ = DeleteObject(bitmap);
            _ = DeleteDC(deviceContext);
        }
    }

    private static void DrawIcon(IntPtr deviceContext, IntPtr icon, int iconPixelSize)
    {
        if (!DrawIconEx(deviceContext, 0, 0, icon, iconPixelSize, iconPixelSize, 0, IntPtr.Zero, DiNormal))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        public fixed char DisplayName[DisplayNameLength];
        public fixed char TypeName[TypeNameLength];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        // RGBQUAD bmiColors[1], present even when the 32-bit BI_RGB color table is unused.
        public uint ColorTableEntry;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nuint SHGetFileInfoW(string path, uint fileAttributes, ref ShellFileInfo fileInfo,
        uint fileInfoSize, uint flags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetImageList(int tier, in Guid interfaceId, out IntPtr imageList);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int AssocQueryStringW(uint flags, uint associationString, string association,
        IntPtr extra, IntPtr output, ref uint outputCharacterCount);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, uint concurrencyModel);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr deviceContext, ref BitmapInfo info,
        uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DrawIconEx(IntPtr deviceContext, int x, int y, IntPtr icon,
        int width, int height, uint step, IntPtr flickerFreeDraw, uint flags);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

}
