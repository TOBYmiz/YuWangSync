using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using YuWangCloudSync.Core;
using YuWangCloudSync.Crypto;
using YuWangCloudSync.Models;

namespace YuWangCloudSync.Net;

/// <summary>
/// 育网云 HTTP 客户端：登录 / 列目录 / 下载文件。
///
/// **关键设计（踩坑总结）**：
/// 1. 禁用 CookieContainer 自动管理（UseCookies=false），
///    改为手工提取 Set-Cookie 的 JSESSIONID 并在后续请求手动加 Cookie 头。
///    原因：.NET CookieContainer 对 IP 字面量主机的 cookie 匹配存在 quirks，
///    与 Python http.cookiejar 的 domain_initial_dot 坑同源。Python 侧用直接
///    Cookie 头绕过后 100% 成功，C# 侧沿用同一策略。
/// 2. 所有请求带 Referer（服务器可能有校验）。
/// </summary>
public sealed class YuWangClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private string? _jsessionid;   // 手工管理的会话 ID

    /// <summary>登录/列目录等轻量请求的超时</summary>
    private readonly TimeSpan _requestTimeout;
    /// <summary>单文件下载超时（直链分片下载，大文件 30s 远远不够）</summary>
    private readonly TimeSpan _downloadTimeout;
    private readonly CancellationToken _lifetimeToken;
    // The server keeps directory navigation context in the session. Keep list + permission
    // check adjacent so concurrent Explorer enumerations cannot probe another directory.
    private readonly SemaphoreSlim _folderContextGate = new(1, 1);
    private static readonly TimeSpan RangeStallTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RangeRetryBaseDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RangeRetryMaxDelay = TimeSpan.FromSeconds(5);
    private const int RangeRetryCount = 3;
    private const int RangeChunkSize = 4 * 1024 * 1024;
    private const int RangeMaxConcurrency = 4;

    public string Server { get; }
    public string Username { get; private set; }
    public string Password { get; private set; }
    public bool IsLoggedIn { get; private set; }
    public string? SessionId => _jsessionid;

    public YuWangClient(string server, string username, string password, Action<string>? log = null,
        int requestTimeoutSeconds = 30, int downloadTimeoutMinutes = 10,
        CancellationToken lifetimeToken = default)
    {
        Server = server.TrimEnd('/');
        Username = username;
        Password = password;
        _log = log ?? (msg => Console.Error.WriteLine(msg));
        _requestTimeout = TimeSpan.FromSeconds(requestTimeoutSeconds);
        _downloadTimeout = TimeSpan.FromMinutes(downloadTimeoutMinutes);
        _lifetimeToken = lifetimeToken;

        var handler = new HttpClientHandler
        {
            UseCookies = false,           // 关键：禁用自动 cookie
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 8
        };
        // 不用 HttpClient.Timeout 统一卡死：改由每个请求自带 CTS 控制，
        // 轻量请求 30s、下载 10min，否则 56MB 文件必然超时。
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(Server),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) {ProductIdentity.DisplayNameEn}/{ProductVersion.Current}");
    }

    public void ConfigureCredentials(string username, string password)
    {
        Username = username ?? throw new ArgumentNullException(nameof(username));
        Password = password ?? throw new ArgumentNullException(nameof(password));
    }

    /// <summary>统一发送入口：按用途给不同超时</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, TimeSpan timeout,
        HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead,
        CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetimeToken);
        cts.CancelAfter(timeout);
        return await _http.SendAsync(req, completion, cts.Token).ConfigureAwait(false);
    }

    /// <summary>给请求附加手工 Cookie 头（若有 session）</summary>
    private HttpRequestMessage MakeRequest(HttpMethod method, string url, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (content != null) req.Content = content;
        req.Headers.Referrer = new Uri(Server + "/main.jsx");
        if (!string.IsNullOrEmpty(_jsessionid))
            req.Headers.TryAddWithoutValidation("Cookie", $"JSESSIONID={_jsessionid}");
        return req;
    }

    /// <summary>从响应头提取 Set-Cookie 中的 JSESSIONID</summary>
    private void CaptureSession(HttpResponseMessage resp)
    {
        if (resp.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var v in values)
            {
                var m = Regex.Match(v, @"JSESSIONID=([^;]+)", RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    var newSid = m.Groups[1].Value.Trim();
                    if (newSid != _jsessionid)
                    {
                        _log("[SESSION] 会话 ID 已更新");
                        _jsessionid = newSid;
                    }
                }
            }
        }
    }

    // ================= 启动期：登录 =================

    /// <summary>GET /welcome.jsx 建立会话（拿 JSESSIONID）</summary>
    public async Task EstablishSessionAsync()
    {
        using var req = MakeRequest(HttpMethod.Get, "/welcome.jsx");
        using var resp = await SendAsync(req, _requestTimeout);
        CaptureSession(resp);
        var body = await resp.Content.ReadAsStringAsync();
        _log($"[DEBUG] welcome.jsx status={resp.StatusCode} len={body.Length} session={(!string.IsNullOrEmpty(_jsessionid) ? "已获取" : "未获取")}");
        IsLoggedIn = false;
    }

    /// <summary>GET /servlet/VerificationCode，返回验证码 PNG 字节流</summary>
    public async Task<byte[]> DownloadCaptchaAsync()
    {
        using var req = MakeRequest(HttpMethod.Get, "/servlet/VerificationCode");
        using var resp = await SendAsync(req, _requestTimeout);
        CaptureSession(resp);
        var img = await resp.Content.ReadAsByteArrayAsync();
        _log($"[DEBUG] VerificationCode status={resp.StatusCode} size={img.Length} session={(!string.IsNullOrEmpty(_jsessionid) ? "已获取" : "未获取")}");
        return img;
    }

    /// <summary>
    /// 提交登录：RSA 加密账密 + 验证码 POST /main.jsx?ram=<rand>
    /// 成功返回 true（响应非登录页），失败返回 false。
    /// </summary>
    public async Task<bool> LoginAsync(string captchaCode)
    {
        // RSA 加密：用户名先 Uri.EscapeDataString，密码直接加密
        var el = RsaUtils.Encrypt(Uri.EscapeDataString(Username));
        var ep = RsaUtils.Encrypt(Password);

        var body = $"guest=0&encryptloginname={Uri.EscapeDataString(el)}" +
                   $"&encryptpassword={Uri.EscapeDataString(ep)}" +
                   $"&validate={Uri.EscapeDataString(captchaCode)}";

        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var req = MakeRequest(HttpMethod.Post, $"/main.jsx?ram={Random.Shared.NextDouble()}", content);
        using var resp = await SendAsync(req, _requestTimeout);
        CaptureSession(resp);

        var html = await resp.Content.ReadAsStringAsync();
        _log($"[DEBUG] main.jsx status={resp.StatusCode} finalUrl={resp.RequestMessage?.RequestUri} len={html.Length} session={(!string.IsNullOrEmpty(_jsessionid) ? "已获取" : "未获取")}");

        // 诊断：输出服务器提示（init() 里的 layer.alert 内容）
        var alert = Regex.Match(html, @"layer\.alert\(\s*""([^""]+)""");
        if (alert.Success)
            _log($"[DEBUG] 服务器提示: {alert.Groups[1].Value}");

        IsLoggedIn = !HtmlParser.IsLoginPage(html);
        _log($"[DEBUG] 登录判定: {(IsLoggedIn ? "成功" : "失败（仍是登录页）")}");
        return IsLoggedIn;
    }

    /// <summary>交互式登录：建立 session → 下载验证码到本地文件 → 等用户输入 → 提交</summary>
    public async Task<bool> LoginInteractivelyAsync(string captchaPath, Func<string> readCodeFromUser)
    {
        var img = await RefreshLoginCaptchaAsync();
        await File.WriteAllBytesAsync(captchaPath, img);
        _log($"[INFO] 验证码已保存到 {captchaPath}（请放大 6 倍识别，注意字母 I 与数字 1）");
        var code = readCodeFromUser();
        return await SubmitLoginCaptchaAsync(code);
    }

    public async Task<byte[]> RefreshLoginCaptchaAsync()
    {
        await EstablishSessionAsync();
        return await DownloadCaptchaAsync();
    }

    public async Task<bool> SubmitLoginCaptchaAsync(string captchaCode)
    {
        var ok = await LoginAsync(captchaCode);
        if (ok) await InitializeWorkspaceAsync();
        return ok;
    }

    /// <summary>
    /// 登录后初始化云盘工作区上下文（**关键步骤，否则列目录一律返回"温馨提醒"页/500**）。
    /// 浏览器登录成功后 main.jsx 页面会自动加载一组 sysFrameAction.do frame，
    /// 服务器在这些请求里初始化 session 级的云盘上下文；跳过它们直接调
    /// /fmsFolderInfoIconAction.do 会得到 1093 字节的"温馨提醒"提示页。
    /// </summary>
    public async Task InitializeWorkspaceAsync()
    {
        foreach (var frame in new[] { "frame_desk", "frame_folder", "frame_module", "frame_welcome" })
        {
            try
            {
                using var req = MakeRequest(HttpMethod.Get, $"/sysFrameAction.do?method={frame}");
                using var resp = await SendAsync(req, _requestTimeout);
                CaptureSession(resp);
                var len = (await resp.Content.ReadAsByteArrayAsync()).Length;
                _log($"[DEBUG] 初始化 frame {frame} status={resp.StatusCode} len={len}");
            }
            catch (Exception ex)
            {
                _log($"[WARN] 初始化 frame {frame} 异常: {ex.Message}");
            }
        }
    }

    // ================= 列目录 =================

    /// <summary>
    /// GET /fmsFolderInfoIconAction.do?method=list&diskid=&folderid=
    /// 注意：必须带 diskid 参数，否则服务器返回 500（已踩透）
    /// </summary>
    public async Task<string> FetchFolderHtmlAsync(ulong diskId, ulong folderId)
    {
        await _folderContextGate.WaitAsync(_lifetimeToken).ConfigureAwait(false);
        try { return await FetchFolderHtmlCoreAsync(diskId, folderId).ConfigureAwait(false); }
        finally { _folderContextGate.Release(); }
    }

    public async Task<List<YuWangItem>> ListFolderAsync(ulong diskId, ulong folderId)
    {
        var result = await ListFolderWithPermissionsAsync(diskId, folderId).ConfigureAwait(false);
        return result.Items.ToList();
    }

    public async Task<FolderListingResult> ListFolderWithPermissionsAsync(ulong diskId, ulong folderId)
    {
        await _folderContextGate.WaitAsync(_lifetimeToken).ConfigureAwait(false);
        try
        {
            var html = await FetchFolderHtmlCoreAsync(diskId, folderId).ConfigureAwait(false);
            var showItemCount = System.Text.RegularExpressions.Regex.Matches(html, @"showItem\('").Count;
            _log($"[DEBUG] ListFolder disk={diskId} folder={folderId} htmlLen={html.Length} showItem={showItemCount}");
            if (HtmlParser.IsLoginPage(html))
                _log($"[WARN] ListFolder disk={diskId} folder={folderId} 返回登录页 → session 已失效");
            // Do not retain response HTML: directory enumeration is frequent and the response
            // can contain account-specific page state.
            if (showItemCount == 0 || html.Length < 2000)
                _log($"[WARN] ListFolder disk={diskId} folder={folderId} 疑似异常页（showItem={showItemCount}, len={html.Length}）");

            var items = HtmlParser.ExtractItems(html);
            var validPage = IsExpectedFolderPage(html, diskId, folderId);
            var permission = DirectoryPermission.Unknown;
            if (validPage)
            {
                try
                {
                    var pasteResponse = await CheckBeforePasteCoreAsync().ConfigureAwait(false);
                    permission = HtmlParser.ParseDirectoryPermission(html, pasteResponse, diskId, folderId);
                }
                catch (Exception error)
                {
                    _log($"[PERMISSION] disk={diskId} folder={folderId} beforePaste 探测失败，权限未知: {error.Message}");
                }
            }
            _log($"[PERMISSION] disk={diskId} folder={folderId} result={permission}");
            return new FolderListingResult(items, permission, validPage);
        }
        finally { _folderContextGate.Release(); }
    }

    private async Task<string> FetchFolderHtmlCoreAsync(ulong diskId, ulong folderId)
    {
        using var req = MakeRequest(HttpMethod.Get,
            $"/fmsFolderInfoIconAction.do?method=list&diskid={diskId}&folderid={folderId}");
        using var resp = await SendAsync(req, _requestTimeout).ConfigureAwait(false);
        CaptureSession(resp);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    private async Task<string> CheckBeforePasteCoreAsync()
    {
        var content = new ByteArrayContent(Array.Empty<byte>());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
        using var req = MakeRequest(HttpMethod.Post,
            "/fmsFolderInfoIconAction.do?method=beforePaste", content);
        using var resp = await SendAsync(req, _requestTimeout).ConfigureAwait(false);
        CaptureSession(resp);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    public async Task<DirectoryPermission> ProbeDirectoryPermissionAsync(ulong diskId, ulong folderId)
    {
        var result = await ListFolderWithPermissionsAsync(diskId, folderId).ConfigureAwait(false);
        return result.Permission;
    }

    /// <summary>Delete one cloud item after rechecking write capability in its parent directory.</summary>
    public async Task<bool> DeleteRemoteItemAsync(ulong diskId, ulong parentFolderId, ulong itemId, bool isFolder)
    {
        await _folderContextGate.WaitAsync(_lifetimeToken).ConfigureAwait(false);
        try { return await DeleteRemoteItemCoreAsync(diskId, parentFolderId, itemId, isFolder).ConfigureAwait(false); }
        finally { _folderContextGate.Release(); }
    }

    /// <summary>Rename an item in place or move it through the server's beforeCut/beforePaste flow.</summary>
    public async Task<bool> RenameRemoteItemAsync(ulong diskId, ulong sourceFolderId, ulong itemId,
        bool isFolder, ulong? targetFolderId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return false;
        await _folderContextGate.WaitAsync(_lifetimeToken).ConfigureAwait(false);
        try
        {
            var sourceHtml = await FetchWritableFolderCoreAsync(diskId, sourceFolderId).ConfigureAwait(false);
            if (sourceHtml is null) return false;
            var sourceFields = HtmlParser.ExtractPageFormFields(sourceHtml);
            if (sourceFields.Count == 0) return false;

            if (targetFolderId is null)
                return await DeleteRemoteItemCoreAsync(diskId, sourceFolderId, itemId, isFolder, sourceHtml)
                    .ConfigureAwait(false);

            if (targetFolderId.Value == sourceFolderId)
            {
                sourceFields.Add(new KeyValuePair<string, string>("fid", itemId.ToString()));
                sourceFields.Add(new KeyValuePair<string, string>("newname", newName));
                var method = isFolder ? "changeFolderName" : "changeFileName";
                var endpoint = isFolder ? "/fmsFolderInfoAction.do" : "/fmsFileInfoAction.do";
                var response = await PostFormCoreAsync($"{endpoint}?method={method}", sourceFields).ConfigureAwait(false);
                if (!response.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return false;
                var verifyHtml = await FetchFolderHtmlCoreAsync(diskId, sourceFolderId).ConfigureAwait(false);
                return HtmlParser.ExtractItems(verifyHtml).Any(item => item.Id == itemId && item.Name == newName);
            }

            sourceFields.Add(new KeyValuePair<string, string>("checkids", $"{(isFolder ? "D" : "F")}_{itemId};"));
            var cutResponse = await PostFormCoreAsync(
                "/fmsFolderInfoIconAction.do?method=beforeCut", sourceFields).ConfigureAwait(false);
            if (!cutResponse.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return false;

            var targetHtml = await FetchFolderHtmlCoreAsync(diskId, targetFolderId.Value).ConfigureAwait(false);
            if (!IsExpectedFolderPage(targetHtml, diskId, targetFolderId.Value)) return false;
            var pasteResponse = await CheckBeforePasteCoreAsync().ConfigureAwait(false);
            if (HtmlParser.ParseDirectoryPermission(targetHtml, pasteResponse, diskId, targetFolderId.Value)
                != DirectoryPermission.FullAccess
                || !pasteResponse.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return false;

            var targetFields = HtmlParser.ExtractPageFormFields(targetHtml);
            if (targetFields.Count == 0) return false;
            var pasteResult = await PostFormCoreAsync(
                "/fmsFolderInfoIconAction.do?method=pasteRecord", targetFields).ConfigureAwait(false);
            if (HtmlParser.IsLoginPage(pasteResult) || pasteResult.Trim().Equals("forbidden", StringComparison.OrdinalIgnoreCase))
                return false;

            var destination = await FetchFolderHtmlCoreAsync(diskId, targetFolderId.Value).ConfigureAwait(false);
            var source = await FetchFolderHtmlCoreAsync(diskId, sourceFolderId).ConfigureAwait(false);
            return HtmlParser.ExtractItems(destination).Any(item => item.Id == itemId)
                && !HtmlParser.ExtractItems(source).Any(item => item.Id == itemId);
        }
        finally { _folderContextGate.Release(); }
    }

    private async Task<bool> DeleteRemoteItemCoreAsync(ulong diskId, ulong parentFolderId, ulong itemId,
        bool isFolder, string? alreadyFetchedHtml = null)
    {
        var html = alreadyFetchedHtml ?? await FetchWritableFolderCoreAsync(diskId, parentFolderId).ConfigureAwait(false);
        if (html is null) return false;
        var fields = HtmlParser.ExtractPageFormFields(html);
        if (fields.Count == 0) return false;
        fields.Add(new KeyValuePair<string, string>("checkid", $"{(isFolder ? "D" : "F")}_{itemId}"));
        var response = await PostFormCoreAsync(
            "/fmsFolderInfoIconAction.do?method=delBatchRecord", fields).ConfigureAwait(false);
        if (HtmlParser.IsLoginPage(response) || response.Trim().Equals("forbidden", StringComparison.OrdinalIgnoreCase))
            return false;
        var verifyHtml = await FetchFolderHtmlCoreAsync(diskId, parentFolderId).ConfigureAwait(false);
        return IsExpectedFolderPage(verifyHtml, diskId, parentFolderId)
            && !HtmlParser.ExtractItems(verifyHtml).Any(item => item.Id == itemId);
    }

    private async Task<string?> FetchWritableFolderCoreAsync(ulong diskId, ulong folderId)
    {
        var html = await FetchFolderHtmlCoreAsync(diskId, folderId).ConfigureAwait(false);
        if (!IsExpectedFolderPage(html, diskId, folderId)) return null;
        var pasteResponse = await CheckBeforePasteCoreAsync().ConfigureAwait(false);
        return HtmlParser.ParseDirectoryPermission(html, pasteResponse, diskId, folderId)
            == DirectoryPermission.FullAccess ? html : null;
    }

    private static bool IsExpectedFolderPage(string html, ulong diskId, ulong folderId) =>
        !HtmlParser.IsLoginPage(html)
        && HtmlParser.TryGetHiddenUInt64(html, "diskid", out var actualDisk)
        && HtmlParser.TryGetHiddenUInt64(html, "folderid", out var actualFolder)
        && actualDisk == diskId && actualFolder == folderId;

    private async Task<string> PostFormCoreAsync(string endpoint,
        IEnumerable<KeyValuePair<string, string>> fields)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var req = MakeRequest(HttpMethod.Post, endpoint, content);
        using var resp = await SendAsync(req, _requestTimeout).ConfigureAwait(false);
        CaptureSession(resp);
        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
    }

    // ================= 磁盘树（容器 → 磁盘）=================
    //
    // 项目映射值 0 从 frame_desk 读取服务端根节点；该账号根页的 showDisk('100','1') 对应容器 typeid=100。
    // 服务端结构继续是"容器(typeid) → 磁盘(diskid)"两级：
    //   班级共享(typeid=100)
    //     ├─ 24腾飞班级(typeid=101) → TF2428(diskid=2540)
    //     ├─ 年级共享(typeid=106)   → 24腾飞年级(diskid=2555)
    //     └─ 校级共享(typeid=102)   → 资料共享(2545)/语文(2558)/…/计算机(2568)
    // 顶层容器（如 100）的面板里是 showType('101') 调用；子容器面板里是 showDisk('2540') 调用。
    // 这里从 RootContainerTypeId 递归拉取整棵磁盘树；值为 0 时先解析 frame_desk 根页。
    // 解析失败不再落盘 HTML（2026-10-09 取消，见 FetchLevel / ListFolderAsync 注释），
    // 诊断一律走日志：len / isLoginPage / 解析到的节点数三项足以区分 session 失效与正则失配。

    /// <summary>typeid=0 是项目内的服务端根目录映射，使用 frame_desk；正数使用容器列表接口。</summary>
    public async Task<string> FetchDiskTreeHtmlAsync(ulong typeid)
    {
        string? lastHtml = null;
        var urls = typeid == 0
            ? new[] { "/sysFrameAction.do?method=frame_desk" }
            : new[]
        {
            $"/fmsDiskInfoIndexAction.do?typeid={typeid}",
            $"/fmsDiskInfoIndexAction.do?method=list&typeid={typeid}"
        };
        foreach (var url in urls)
        {
            try
            {
                using var req = MakeRequest(HttpMethod.Get, url);
                using var resp = await SendAsync(req, _requestTimeout);
                CaptureSession(resp);
                var html = await resp.Content.ReadAsStringAsync();
                if (!HtmlParser.IsLoginPage(html))
                {
                    _log($"[DISKTREE] typeid={typeid} 拉取成功 via {url} len={html.Length}");
                    return html;
                }
                lastHtml = html;
                _log($"[DISKTREE] typeid={typeid} {url} 返回登录页（session 可能失效）");
            }
            catch (Exception ex)
            {
                _log($"[DISKTREE] typeid={typeid} 拉取 {url} 异常: {ex.Message}");
            }
        }
        return lastHtml ?? "";
    }

    /// <summary>从面板 HTML 解析 showType/showDisk 调用，返回子节点（容器或磁盘）。根页调用可把 showDisk 当作容器。
    /// 真实面板结构（已实证）：调用点 ondblclick="javascript:showType('101')" 为单参数，
    /// 名称在其后的 span 标签里（span 的 id 属性与节点 id 精确对应，如 span id="101" 内容为"24腾飞班级"）。</summary>
    public List<(ulong id, string type, string name)> ParseDiskTree(string html, bool rootListing = false)
    {
        var result = new List<(ulong id, string type, string name)>();
        if (string.IsNullOrEmpty(html)) return result;
        // 同时匹配 showType('101') / showType('101','名称') / showDisk('2540') / showDisk('100','1')
        var re = new Regex(@"(showType|showDisk)\('(\d+)'(?:,\s*'([^']*)')?\)", RegexOptions.IgnoreCase);
        var seen = new HashSet<ulong>();
        foreach (Match m in re.Matches(html))
        {
            var kind = !rootListing && m.Groups[1].Value.Equals("showDisk", StringComparison.OrdinalIgnoreCase)
                ? "disk" : "container";
            if (!ulong.TryParse(m.Groups[2].Value, out ulong id)) continue;
            if (!seen.Add(id)) continue;

            string name = "";
            var nameParam = m.Groups[3].Value?.Trim() ?? "";
            // 1) 优先用调用里的第 2 个字符串参数（如 showType('101','24腾飞班级')），但排除纯数字编号
            if (nameParam.Length > 0 && !Regex.IsMatch(nameParam, @"^\d+$"))
                name = nameParam;
            // 2) 真实结构（已实证两层面板一致）：名称在 span id="节点id" 的文本里，如 span id="2540" → "TF2428"
            if (string.IsNullOrEmpty(name))
            {
                var sm = Regex.Match(html,
                    $@"<span\b[^>]*\bid\s*=\s*[""']{id}[""'][^>]*>\s*([^<]{{1,80}}?)\s*</span>",
                    RegexOptions.IgnoreCase);
                if (sm.Success)
                    name = System.Net.WebUtility.HtmlDecode(sm.Groups[1].Value).Trim();
            }
            // 3) 调用点之后窗口内找锚点文本（名称距调用点约 600 字符，窗口取 1200）
            if (string.IsNullOrEmpty(name))
            {
                var seg = html.Substring(m.Index, Math.Min(1200, html.Length - m.Index));
                var tm = Regex.Match(seg, @">([^<>]{1,60})<");
                while (tm.Success)
                {
                    var cand = tm.Groups[1].Value.Trim();
                    if (cand.Length > 0 && !cand.Contains("showType", StringComparison.OrdinalIgnoreCase)
                        && !cand.Contains("showDisk", StringComparison.OrdinalIgnoreCase)
                        && !cand.Contains("javascript", StringComparison.OrdinalIgnoreCase))
                    {
                        name = cand;
                        break;
                    }
                    tm = tm.NextMatch();
                }
            }
            // 4) 兜底用 id
            if (string.IsNullOrEmpty(name)) name = id.ToString();
            result.Add((id, kind, name));
        }
        return result;
    }

    /// <summary>从根容器递归拉取整棵磁盘树（扁平列表，含父子关系 parent_id）</summary>
    public async Task<List<DiskTreeNode>> FetchDiskTreeAsync(ulong rootTypeId)
    {
        var flat = new List<DiskTreeNode>();
        await FetchLevel(rootTypeId, flat, 0);
        return flat;
    }

    private async Task FetchLevel(ulong typeid, List<DiskTreeNode> outNodes, int depth)
    {
        if (depth > 6) return;  // 防御性上限，避免异常递归
        string html;
        try { html = await FetchDiskTreeHtmlAsync(typeid); }
        catch (Exception ex)
        {
            _log($"[DISKTREE] 拉取容器 typeid={typeid} 失败: {ex.Message}");
            return;
        }
        var nodes = ParseDiskTree(html, rootListing: typeid == 0);
        if (nodes.Count == 0)
        {
            // 同 ListFolderAsync：只打日志，不再落盘 HTML（dump_disktree_*.html 已于 2026-10-09 取消）。
            // len 与是否登录页足以定位是 session 失效还是正则失配，需要样本时另行抓包。
            _log($"[DISKTREE] 容器 typeid={typeid} 未解析到任何子节点"
                + $"（len={html.Length}, isLoginPage={HtmlParser.IsLoginPage(html)}），已跳过该分支");
            return;
        }
        foreach (var n in nodes)
        {
            outNodes.Add(new DiskTreeNode { Id = n.id, NodeType = n.type, Name = n.name, ParentId = typeid });
            if (n.type == "container")
                await FetchLevel(n.id, outNodes, depth + 1);
        }
    }

    // ================= 下载文件 =================
    //
    // **旧路径（downSelectFile 整文件下载 + FileDownloadCache 落盘再切分）已于 2026-09-29 移除**：
    // 该接口忽略 Range、chunked 无 Content-Length，任何一次 FETCH_DATA 都得把整文件下完，
    // 还得先落 D:\YuWangCache 再读回（42MB 复制实测 16.4s）。
    // 现统一走下面"直链 Range 按需分片"：探测拿直链 → 每次只拉本次回调要的那一段。

    // ================= 真实大小探测 =================

    /// <summary>
    /// 探测文件的**精确字节数**并返回静态直链。
    ///
    /// **背景（2026-09-29 实测确认）**：
    ///   - 列表页大小是向下取整到整 MB 的声明值，占位用它创建 → 首次复制/读取拿到错误大小；
    ///   - downSelectFile / downFile 是 chunked 响应（无 Content-Length）且忽略 Range，
    ///     "发起请求后立即停止"拿不到总大小；
    ///   - 但 fmsPicPlayerAction.jsx（对**任意类型**文件都返回页面）里引用了
    ///     upload_dir/sketch/&lt;yyyyMM&gt;/&lt;hash&gt;.png，把 sketch 换成 file、
    ///     扩展名换成真实扩展名，即得原文件静态直链；
    ///   - 直链 HEAD 返回精确 Content-Length，且 Accept-Ranges: bytes（支持断点续传，
    ///     留作后续分片水化优化）。
    ///
    /// 探测成本：2 个轻量请求（PicPlayer 页面 ~5KB + HEAD 无 body），局域网 &lt;100ms。
    /// 失败（页面无 hash / 直链 404 / 无 Content-Length）返回 null，调用方降级用声明值+余量。
    /// </summary>
    public async Task<(long Size, string DirectUrl, bool IsContentless)?> ProbeRealSizeAsync(
        ulong fileId, string fileName, long? declaredSize = null)
    {
        // 1. PicPlayer 页面提取 sketch hash（对视频/音频/文档/任意文件均可用）
        using var req = MakeRequest(HttpMethod.Get, $"/fmsPicPlayerAction.jsx?id={fileId}");
        using var resp = await SendAsync(req, _requestTimeout).ConfigureAwait(false);
        CaptureSession(resp);
        if (!resp.IsSuccessStatusCode) return null;
        var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var m = Regex.Match(html, @"upload_dir/sketch/(\d{6})/([0-9a-f]{32})\.png", RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        // 2. 替换路径段得到原文件直链（hash 与 yyyyMM 目录一致，已用 477748/503392 双重验证）
        var ext = Path.GetExtension(fileName.AsSpan()).TrimStart('.').ToString().ToLowerInvariant();
        var directUrl = $"/upload_dir/file/{m.Groups[1].Value}/{m.Groups[2].Value}" +
                        (ext.Length > 0 ? "." + ext : "");

        // 3. HEAD 直链拿精确 Content-Length（直链为静态资源，无需 Cookie）
        using var head = new HttpRequestMessage(HttpMethod.Head, directUrl);
        using var hresp = await SendAsync(head, _requestTimeout).ConfigureAwait(false);
        // Some server entries retain metadata/name after the uploaded object has disappeared.
        // A 404 is treated as an empty file only when the directory listing also declared 0 B;
        // otherwise it remains a normal probe failure.
        if (hresp.StatusCode == HttpStatusCode.NotFound && declaredSize == 0)
            return (0, directUrl, true);
        if (!hresp.IsSuccessStatusCode) return null;
        var len = hresp.Content.Headers.ContentLength;
        if (len is null) return null;
        if (len.Value == 0)
            return declaredSize == 0 ? (0, directUrl, true) : null;
        return (len.Value, directUrl, false);
    }

    // ================= 直链 Range 分片下载（水化主路径）=================

    /// <summary>
    /// 用静态直链取文件的 [offset, offset+length) 范围；范围大于 4 MB 时按 4 MB 拆分，最多 4 路并发。
    ///
    /// **为什么取代 downSelectFile**：后者是 chunked 且忽略 Range，任何一次 FETCH_DATA
    /// 都必须把整文件下载到 D:\YuWangCache 再切分——打开一个 95MB 的 PPTX 哪怕只读头几个
    /// 分片，也要先把 95MB 全下完（实测 42MB 复制耗时 16.4s）。
    /// 直链（upload_dir/file/...）实测 Accept-Ranges: bytes，可**按需**只拉本次回调要的那一段。
    ///
    /// total 来源：206 时从 Content-Range 的 /&lt;total&gt; 解析（精确）；
    /// 若服务器忽略 Range 返回 200，则视为整文件，本地切片并 total=全文件长度。
    /// 连续 5 秒没有响应或数据就重试，最多重试 3 次；HTTP 408、429 和 5xx 也按临时错误处理，
    /// 并使用有上限的退避（参考 Retry-After，单次等待最多 5 秒）。单次操作总时限遵循
    /// DownloadTimeoutMinutes 配置；读写期间持续报告进度，避免固定短时限中断大范围传输。
    /// </summary>
    public async Task<(byte[] Data, long Total)?> DownloadRangeDirectAsync(string directUrl, long offset, long length,
        Action<long, long>? reportProgress = null)
    {
        var operationTimeoutLimit = _downloadTimeout;
        using var operationDeadline = new CancellationTokenSource(operationTimeoutLimit);
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeToken, operationDeadline.Token);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await DownloadRangeOperationAttemptAsync(
                            directUrl, offset, length, operationTimeout.Token, reportProgress)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (
                    attempt < RangeRetryCount &&
                    !_lifetimeToken.IsCancellationRequested &&
                    !operationDeadline.IsCancellationRequested &&
                    (IsRetryableRangeFailure(ex) || ex is OperationCanceledException))
                {
                    var retryFailure = ex is OperationCanceledException canceled
                        ? new TimeoutException("Range 请求意外取消", canceled)
                        : ex;
                    var delay = GetRangeRetryDelay(retryFailure, attempt);
                    _log($"[RANGE] offset={offset} length={length} 临时失败，{delay.TotalMilliseconds:F0}ms 后重试 " +
                         $"({attempt + 1}/{RangeRetryCount}): {retryFailure.Message}");
                    await Task.Delay(delay, operationTimeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex) when (operationDeadline.IsCancellationRequested)
        {
            _log($"[RANGE] offset={offset} length={length} 下载总时限到期：{elapsed.Elapsed.TotalSeconds:F1}s / " +
                 $"{operationTimeoutLimit.TotalSeconds:F0}s");
            throw new TimeoutException(
                $"Range 下载超过配置的总时限（{operationTimeoutLimit.TotalMinutes:F1} 分钟）", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new TimeoutException("Range 请求被网络层意外取消", ex);
        }
    }

    private async Task<(byte[] Data, long Total)?> DownloadRangeOperationAttemptAsync(string directUrl, long offset,
        long length, CancellationToken operationToken, Action<long, long>? reportProgress)
    {
        var firstLength = length > 0 ? Math.Min(length, RangeChunkSize) : length;
        long firstChunkCompleted = 0;
        long availableLength = 0;
        long totalLength = 0;
        byte[]? data = null;
        long[]? completedByChunk = null;
        var chunkCount = 1;
        Task[] workerTasks = [];
        var nextChunk = 0;
        var progressGate = new object();
        using var chunkCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationToken);

        void ReportFirstChunkProgress(long _, long completed)
        {
            lock (progressGate)
            {
                firstChunkCompleted = Math.Max(firstChunkCompleted, completed);
                if (completedByChunk is not null)
                    completedByChunk[0] = firstChunkCompleted;
                var aggregateCompleted = completedByChunk?.Sum() ?? firstChunkCompleted;
                reportProgress?.Invoke(length, Math.Min(length, aggregateCompleted));
            }
        }

        async Task DownloadWorkerAsync()
        {
            while (true)
            {
                var chunkIndex = Interlocked.Increment(ref nextChunk);
                if (chunkIndex >= chunkCount) return;

                var chunkOffset = (long)chunkIndex * RangeChunkSize;
                var chunkLength = Math.Min(RangeChunkSize, availableLength - chunkOffset);
                void ReportChunkProgress(long _, long completed)
                {
                    lock (progressGate)
                    {
                        completedByChunk![chunkIndex] = Math.Max(completedByChunk[chunkIndex], completed);
                        reportProgress?.Invoke(length, Math.Min(length, completedByChunk!.Sum()));
                    }
                }

                try
                {
                    var chunk = await DownloadRangeRequestAsync(directUrl, offset + chunkOffset, chunkLength,
                            chunkCancellation.Token, ReportChunkProgress, reportWholeFileProgress: null,
                            onRangeAccepted: null)
                        .ConfigureAwait(false);
                    if (chunk is null)
                        throw new IOException($"Range 分片请求失败：offset={offset + chunkOffset}");
                    if (chunk.IsWholeFile)
                        throw new IOException("Range 下载的后续分片被服务器作为整文件响应，已停止并发下载");
                    if (chunk.Total != totalLength)
                        throw new IOException($"Range 分片总长度不一致：首片={totalLength} 后续={chunk.Total}");

                    var expectedLength = Math.Min(chunkLength, availableLength - chunkOffset);
                    if (chunk.Data.Length != expectedLength)
                        throw new IOException(
                            $"Range 分片长度不匹配：offset={offset + chunkOffset} 期望={expectedLength} 实得={chunk.Data.Length}");
                    Buffer.BlockCopy(chunk.Data, 0, data!, (int)chunkOffset, chunk.Data.Length);
                }
                catch
                {
                    chunkCancellation.Cancel();
                    throw;
                }
            }
        }

        void OnFirstRangeAccepted(long serverFileLength)
        {
            totalLength = serverFileLength;
            availableLength = Math.Min(length, totalLength - offset);
            if (availableLength < 0 || availableLength > int.MaxValue)
                throw new IOException($"Range 响应范围过大或无效：offset={offset} length={length} total={totalLength}");

            data = new byte[(int)availableLength];
            chunkCount = availableLength == 0
                ? 1
                : checked((int)((availableLength - 1) / RangeChunkSize + 1));
            completedByChunk = new long[chunkCount];
            var workerCount = Math.Min(RangeMaxConcurrency - 1, chunkCount - 1);
            workerTasks = Enumerable.Range(0, workerCount).Select(_ => DownloadWorkerAsync()).ToArray();
        }

        // Range 响应头确认服务器遵守 Range 后，立刻启动另外最多 3 个分片，
        // 让首片读取期间也能达到 4 路并发。若首个响应是 200，则只保留这份整文件响应并本地切片。
        var first = await DownloadRangeRequestAsync(directUrl, offset, firstLength, chunkCancellation.Token,
                ReportFirstChunkProgress, reportProgress, OnFirstRangeAccepted)
            .ConfigureAwait(false);
        if (first is null) return null;
        if (first.IsWholeFile)
            return (SliceRange(first.Data, offset, length), first.Total);

        if (data is null || completedByChunk is null)
            throw new IOException("Range 首片已返回，但未初始化分片下载状态");
        Buffer.BlockCopy(first.Data, 0, data, 0, first.Data.Length);

        await Task.WhenAll(workerTasks)
            .ConfigureAwait(false);
        return (data, first.Total);
    }

    private async Task<RangeDownloadResponse?> DownloadRangeRequestAsync(string directUrl, long offset,
        long length, CancellationToken operationToken, Action<long, long>? reportRangeProgress,
        Action<long, long>? reportWholeFileProgress, Action<long>? onRangeAccepted)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, directUrl);
        req.Headers.Referrer = new Uri(Server + "/main.jsx");
        if (length > 0)
            req.Headers.TryAddWithoutValidation("Range", $"bytes={offset}-{offset + length - 1}");

        using var headerTimeout = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
        headerTimeout.CancelAfter(RangeStallTimeout);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerTimeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!operationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待 Range 响应头超过 5 秒", ex);
        }

        using var resp = response;
        if (!resp.IsSuccessStatusCode)
        {
            if (IsTransientRangeStatus(resp.StatusCode))
                throw new RetryableRangeException(
                    $"Range 请求返回临时 HTTP {(int)resp.StatusCode} ({resp.StatusCode})",
                    GetRetryAfter(resp));
            return null;
        }

        if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent)
        {
            var cr = resp.Content.Headers.ContentRange;
            if (cr is null || cr.From is null || cr.To is null || cr.Length is null || cr.Length.Value <= 0 ||
                cr.From.Value != offset || cr.To.Value < cr.From.Value)
                throw new IOException("Range 206 响应缺少有效的 Content-Range");
            var rangeLength = cr.To.Value - cr.From.Value + 1;
            var expectedLength = Math.Min(length, cr.Length.Value - offset);
            if (offset < 0 || offset >= cr.Length.Value || rangeLength != expectedLength)
                throw new IOException(
                    $"Range 206 范围不匹配：请求 {offset}+{length}，响应 {cr.From}-{cr.To}/{cr.Length}");
            if (resp.Content.Headers.ContentLength is { } contentLength && contentLength != rangeLength)
                throw new IOException(
                    $"Range 206 长度不匹配：Content-Range 为 {rangeLength} 字节，Content-Length 为 {contentLength} 字节");
            onRangeAccepted?.Invoke(cr.Length.Value);
            // 206：本片长度已知 → 预分配精确 buffer 流式读入，避免
            // ReadAsByteArrayAsync 内部再攒一份 16MB 缓冲再拷贝的开销
            var data = await ReadFully(resp, rangeLength, operationToken,
                    reportRangeProgress)
                .ConfigureAwait(false);
            return new RangeDownloadResponse(data, cr.Length.Value, IsWholeFile: false);
        }

        // 200：服务器忽略 Range，整文件返回；上层切片。
        var all = await ReadFully(resp, resp.Content.Headers.ContentLength ?? 0, operationToken,
                reportWholeFileProgress)
            .ConfigureAwait(false);
        return new RangeDownloadResponse(all, all.Length, IsWholeFile: true);
    }

    private static byte[] SliceRange(byte[] data, long offset, long length)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length <= 0 || offset >= data.Length) return Array.Empty<byte>();
        var take = (int)Math.Min(length, data.Length - offset);
        var slice = new byte[take];
        Buffer.BlockCopy(data, (int)offset, slice, 0, take);
        return slice;
    }

    private sealed record RangeDownloadResponse(byte[] Data, long Total, bool IsWholeFile);

    private static bool IsRetryableRangeFailure(Exception exception) =>
        exception is TimeoutException or IOException or HttpRequestException;

    private static bool IsTransientRangeStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
        ((int)statusCode >= 500 && (int)statusCode < 600 &&
         statusCode is not HttpStatusCode.NotImplemented and not HttpStatusCode.HttpVersionNotSupported);

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return delta;
        if (retryAfter?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    private static TimeSpan GetRangeRetryDelay(Exception exception, int retryIndex)
    {
        if (exception is RetryableRangeException { RetryAfter: { } retryAfter } && retryAfter > TimeSpan.Zero)
            return retryAfter <= RangeRetryMaxDelay ? retryAfter : RangeRetryMaxDelay;

        var exponentialMs = RangeRetryBaseDelay.TotalMilliseconds * Math.Pow(2, retryIndex);
        var jitterMs = Random.Shared.Next(0, 251);
        return TimeSpan.FromMilliseconds(Math.Min(exponentialMs + jitterMs, RangeRetryMaxDelay.TotalMilliseconds));
    }

    private sealed class RetryableRangeException(string message, TimeSpan? retryAfter) : IOException(message)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    /// <summary>
    /// 把响应体流式读入一个**预分配**的 byte[]。
    /// Content-Length 已知时一次分配到位、边读边填，不再产生中间缓冲与二次拷贝
    /// （16MB 级别的单片拷贝在 3.5MB/s 的链路上是可观的额外开销）。
    /// </summary>
    private static async Task<byte[]> ReadFully(HttpResponseMessage resp, long declaredLength,
        CancellationToken cancellationToken, Action<long, long>? reportProgress = null)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
        var buf = declaredLength > 0 ? new byte[declaredLength] : new byte[1 << 20];
        var read = 0;
        var lastReportedBytes = 0;
        var lastReportedAt = System.Diagnostics.Stopwatch.StartNew();
        if (declaredLength > 0) reportProgress?.Invoke(declaredLength, 0);
        while (true)
        {
            if (read == buf.Length)
            {
                if (declaredLength > 0) break;      // 已按声明长度读满
                Array.Resize(ref buf, buf.Length * 2);
            }
            int n;
            using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readTimeout.CancelAfter(RangeStallTimeout);
                try
                {
                    n = await stream.ReadAsync(buf.AsMemory(read), readTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("Range 响应体连续 5 秒没有数据", ex);
                }
            }
            if (n == 0) break;
            read += n;
            if (declaredLength > 0 &&
                (read == buf.Length || read - lastReportedBytes >= 256 * 1024 ||
                 lastReportedAt.Elapsed >= TimeSpan.FromMilliseconds(250)))
            {
                reportProgress?.Invoke(declaredLength, Math.Min(read, declaredLength));
                lastReportedBytes = read;
                lastReportedAt.Restart();
            }
        }
        if (declaredLength > 0 && read != buf.Length)
            throw new EndOfStreamException($"Range 响应体提前结束：期望 {buf.Length} 字节，实际 {read} 字节");
        if (read != buf.Length) Array.Resize(ref buf, read);
        if (declaredLength > 0 && read == declaredLength && lastReportedBytes != read)
            reportProgress?.Invoke(declaredLength, declaredLength);
        return buf;
    }

    // ================= session 续期 =================

    /// <summary>检测 session 是否还活（GET 根容器面板，未返回登录页即视为有效）</summary>
    public async Task<bool> CheckSessionAsync(ulong rootContainerTypeId = 0)
    {
        try
        {
            var html = await FetchDiskTreeHtmlAsync(rootContainerTypeId);
            IsLoggedIn = !HtmlParser.IsLoginPage(html);
            return IsLoggedIn;
        }
        catch { return false; }
    }

    /// <summary>
    /// 注入已有的 JSESSIONID，用于**复用上次会话**、跳过验证码。
    /// 育网云的 session 有效期较长，每次启动都重新输验证码很麻烦，
    /// 所以把上次的 JSESSIONID 存盘（cookies.json），启动时先试着续用。
    /// </summary>
    public void SetSession(string? jsessionid)
    {
        if (string.IsNullOrWhiteSpace(jsessionid)) return;
        _jsessionid = jsessionid.Trim();
    }

    /// <summary>当前会话 ID（供存盘复用）</summary>
    public string? CurrentSessionId => _jsessionid;

    /// <summary>从 cookies.json（[{"name":"JSESSIONID","value":"..."}]）里读出会话 ID</summary>
    public static string? LoadSessionFrom(string cookiesJsonPath)
    {
        try
        {
            if (!File.Exists(cookiesJsonPath)) return null;
            var json = File.ReadAllText(cookiesJsonPath);
            var m = Regex.Match(json, "\"value\"\\s*:\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        catch { return null; }
    }

    /// <summary>把当前会话 ID 写回 cookies.json，供下次启动复用</summary>
    public void SaveSessionTo(string cookiesJsonPath)
    {
        try
        {
            if (string.IsNullOrEmpty(_jsessionid)) return;
            var json = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new { name = "JSESSIONID", value = _jsessionid, domain = new Uri(Server).Host, path = "/" }
            });
            File.WriteAllText(cookiesJsonPath, json);
        }
        catch { /* 存盘失败不影响运行 */ }
    }

    public void Dispose() => _http.Dispose();
}
