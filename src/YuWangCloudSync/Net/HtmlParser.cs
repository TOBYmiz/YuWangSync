using System.Text;
using System.Text.RegularExpressions;
using YuWangCloudSync.Models;

namespace YuWangCloudSync.Net;

/// <summary>
/// 育网云条目（文件夹/文件），由 HtmlParser 从列表页提取。
/// </summary>
public sealed class YuWangItem
{
    /// <summary>ItemType.D = 文件夹，F = 文件</summary>
    public char Type { get; init; }
    /// <summary>育网云 itemid（showItem 第 2 参数）</summary>
    public ulong Id { get; init; }
    /// <summary>显示名（已去 "文件名：" 前缀和上传时间后缀）</summary>
    public string Name { get; init; } = "";
    /// <summary>字节数（仅文件有，从 "文件大小：14.00 MB" 解析）</summary>
    public long? Size { get; init; }
    /// <summary>上传时间（仅文件有，DateTime? 形式）</summary>
    public DateTime? UploadTime { get; init; }

    public bool IsFolder => Type == 'D';
}

public sealed record FolderListingResult(IReadOnlyList<YuWangItem> Items, DirectoryPermission Permission, bool IsValidPage);

/// <summary>
/// 移植自 list_items.py：解析育网云文件夹列表 HTML。
/// 每个 item 块: showItem('D'|'F','<id>',...) + title="...文件名：xxx"
/// </summary>
public static class HtmlParser
{
    private static readonly string[] WriteToolbarActions = ["new", "delete", "cut", "paste", "upload"];

    /// <summary>
    /// Classify a directory using its actual toolbar links and the read-only beforePaste check.
    /// Empty-clipboard "false", login/error pages, mixed controls, and unknown responses are not
    /// treated as read-only or writable.
    /// </summary>
    public static DirectoryPermission ParseDirectoryPermission(string html, string beforePasteResponse,
        ulong expectedDiskId, ulong expectedFolderId)
    {
        if (string.IsNullOrWhiteSpace(html) || IsLoginPage(html)
            || !TryGetHiddenUInt64(html, "diskid", out var diskId)
            || !TryGetHiddenUInt64(html, "folderid", out var folderId)
            || diskId != expectedDiskId || folderId != expectedFolderId)
            return DirectoryPermission.Unknown;

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match tag in Regex.Matches(html, "<(?<tag>[A-Za-z][A-Za-z0-9]*)\\b(?<attrs>[^>]*)>",
                     RegexOptions.Singleline))
        {
            var classMatch = Regex.Match(tag.Groups["attrs"].Value,
                "\\bclass\\s*=\\s*(['\\\"])(?<value>.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!classMatch.Success) continue;
            var classes = classMatch.Groups["value"].Value.Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var className in classes)
            {
                var action = Regex.Match(className, @"^toolbar_(?<name>new|delete|cut|paste|upload)(?<disabled>1)?$",
                    RegexOptions.IgnoreCase);
                if (!action.Success) continue;
                var name = action.Groups["name"].Value;
                if (action.Groups["disabled"].Success) disabled.Add(name);
            }
        }

        foreach (Match anchor in Regex.Matches(html, "<a\\b(?<attrs>[^>]*)>(?<body>[\\s\\S]*?)</a>",
                     RegexOptions.IgnoreCase))
        {
            var href = Regex.Match(anchor.Groups["attrs"].Value,
                "\\bhref\\s*=\\s*(['\\\"])(?<value>.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!href.Success || !href.Groups["value"].Value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (Match token in Regex.Matches(anchor.Groups["body"].Value,
                         @"\btoolbar_(?<name>new|delete|cut|paste|upload)\b", RegexOptions.IgnoreCase))
                enabled.Add(token.Groups["name"].Value);
        }

        var allEnabled = WriteToolbarActions.All(action => enabled.Contains(action) && !disabled.Contains(action));
        var allDisabled = WriteToolbarActions.All(action => disabled.Contains(action) && !enabled.Contains(action));
        var response = beforePasteResponse.Trim();
        if (allDisabled && response.Equals("forbidden", StringComparison.OrdinalIgnoreCase))
            return DirectoryPermission.ReadOnly;
        if (allEnabled && (response.Equals("true", StringComparison.OrdinalIgnoreCase)
                           || response.Equals("false", StringComparison.OrdinalIgnoreCase)))
            return DirectoryPermission.FullAccess;
        return DirectoryPermission.Unknown;
    }

    public static bool TryGetHiddenUInt64(string html, string fieldName, out ulong value)
    {
        value = 0;
        foreach (Match input in Regex.Matches(html, "<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var name = Regex.Match(input.Value,
                "\\bname\\s*=\\s*(['\\\"])(?<value>.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!name.Success || !name.Groups["value"].Value.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
                continue;
            var fieldValue = Regex.Match(input.Value,
                "\\bvalue\\s*=\\s*(['\\\"])(?<value>.*?)\\1", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (fieldValue.Success && ulong.TryParse(System.Net.WebUtility.HtmlDecode(fieldValue.Groups["value"].Value), out value))
                return true;
            return false;
        }
        return false;
    }

    /// <summary>Read the current pageForm fields needed by the server's legacy write endpoints.</summary>
    public static List<KeyValuePair<string, string>> ExtractPageFormFields(string html)
    {
        var form = Regex.Match(html,
            "<form\\b(?=[^>]*\\b(?:id|name)\\s*=\\s*(['\\\"])pageForm\\1)[^>]*>(?<body>[\\s\\S]*?)</form>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!form.Success) return [];

        var result = new List<KeyValuePair<string, string>>();
        foreach (Match input in Regex.Matches(form.Groups["body"].Value,
                     "<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var name = ReadAttribute(input.Value, "name");
            var type = ReadAttribute(input.Value, "type");
            if (string.IsNullOrWhiteSpace(name)
                || type.Equals("checkbox", StringComparison.OrdinalIgnoreCase)
                || name.Equals("checkid", StringComparison.OrdinalIgnoreCase))
                continue;
            var value = ReadAttribute(input.Value, "value");
            result.Add(new KeyValuePair<string, string>(name, value));
        }
        return result;
    }

    private static string ReadAttribute(string tag, string attribute)
    {
        var match = Regex.Match(tag,
            $"\\b{Regex.Escape(attribute)}\\s*=\\s*(['\\\"])(?<value>.*?)\\1",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value) : "";
    }

    // ============ 两步解析法 ============
    // 为什么不用单一大正则：
    //   原正则用 `.*?` 连接 showItem 块与后面的 title，且末尾还有 `(?:.*?上传时间...)?`，
    //   这个非贪婪组会一直向后扫描到"上传时间"，从而**跨过后面所有 showItem 块**，
    //   把它们吞进第一个匹配里 → 52 个条目只解析出 1 个。
    // 解法：先定位每个 showItem 块，只在「本块起点 → 下一块起点」的限定窗口内找 title，
    //   再用独立小正则从 title 值里提取 name / size / 上传时间。
    //
    // title 值结构（<br> 是**字面文本**分隔符，不是 HTML 标签）：
    //   文件夹：创建者：xxx<br>文件名：一轮复习
    //   文件：  创建者：xxx<br>文件大小：35.00 MB<br>文件名：xxx.mp4<br>上传时间:2025-09-29 08:37:07

    /// <summary>步骤1：定位每个条目块（type + id）</summary>
    private static readonly Regex BlockPattern = new(
        @"showItem\('([A-Z])','(\d+)'", RegexOptions.Compiled);

    /// <summary>步骤2：从条目块窗口内提取 title 属性值</summary>
    private static readonly Regex TitlePattern = new(
        @"title=""([^""]*)""", RegexOptions.Compiled);

    /// <summary>步骤3：从 title 值提取文件名（必须排除 '<'，否则带上 <br> 尾巴成为非法文件名）</summary>
    private static readonly Regex NamePattern = new(
        @"文件名：([^<]+)", RegexOptions.Compiled);

    private static readonly Regex SizePattern = new(
        @"文件大小：([0-9.]+)\s*(\w+)", RegexOptions.Compiled);

    private static readonly Regex TimePattern = new(
        @"上传时间[:：]?\s*(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})", RegexOptions.Compiled);

    /// <summary>title 搜索窗口上限（防极端情况扫描过远）</summary>
    private const int MaxTitleSearchWindow = 4000;

    public static List<YuWangItem> ExtractItems(string html)
    {
        var items = new List<YuWangItem>();
        var seen = new HashSet<ulong>();
        var matches = BlockPattern.Matches(html);

        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            char type = m.Groups[1].Value[0];
            if (!ulong.TryParse(m.Groups[2].Value, out ulong id)) continue;
            if (!seen.Add(id)) continue;

            // 限定搜索窗口：本块起点 → 下一块起点（避免跨块）
            var start = m.Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : html.Length;
            var segment = html[start..Math.Min(end, start + MaxTitleSearchWindow)];

            var titleMatch = TitlePattern.Match(segment);
            if (!titleMatch.Success) continue;
            var title = titleMatch.Groups[1].Value;

            var nameMatch = NamePattern.Match(title);
            if (!nameMatch.Success) continue;
            var name = nameMatch.Groups[1].Value.Trim().TrimEnd(')').Trim();
            if (string.IsNullOrEmpty(name)) continue;

            long? size = null;
            var sizeMatch = SizePattern.Match(title);
            if (sizeMatch.Success &&
                double.TryParse(sizeMatch.Groups[1].Value, out double num))
            {
                size = (long)(num * sizeMatch.Groups[2].Value.ToUpperInvariant() switch
                {
                    "KB" => 1024, "MB" => 1024 * 1024, "GB" => 1024 * 1024 * 1024,
                    _ => 1
                });
            }

            DateTime? uploadTime = null;
            var timeMatch = TimePattern.Match(title);
            if (timeMatch.Success &&
                DateTime.TryParseExact(timeMatch.Groups[1].Value, "yyyy-MM-dd HH:mm:ss",
                    null, System.Globalization.DateTimeStyles.None, out DateTime dt))
            {
                uploadTime = dt;
            }

            items.Add(new YuWangItem
            {
                Type = type,
                Id = id,
                Name = name,
                Size = size,
                UploadTime = uploadTime
            });
        }
        return items;
    }

    /// <summary>从列表 HTML 中提取"当前位置：...>>历史"中的路径名（用于校验）</summary>
    public static string ExtractCurrentPosition(string html)
    {
        var m = Regex.Match(html, @"当前位置：([^<]+)");
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    /// <summary>从列表 HTML 中提取"【 N 个文件夹/ M 个文件】"统计</summary>
    public static (int folders, int files) ExtractCounts(string html)
    {
        var m = Regex.Match(html, @"【\s*(\d+)\s*个文件夹/\s*(\d+)\s*个文件】");
        if (m.Success &&
            int.TryParse(m.Groups[1].Value, out int f) &&
            int.TryParse(m.Groups[2].Value, out int n))
            return (f, n);
        return (0, 0);
    }

    /// <summary>判断响应是否为登录页（用于 session 失效检测）</summary>
    public static bool IsLoginPage(string html) =>
        html.Contains("请输入登录名") || html.Contains("layer.alert(\"验证码错误");
}
