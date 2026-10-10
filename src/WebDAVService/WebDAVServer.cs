using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Serilog;

public class WebDAVServer
{
    private readonly WebDAVConfig _config;
    private readonly Dictionary<string, string> _users;
    private readonly Dictionary<string, bool> _readonlyMap;
    private readonly SessionManager _sessions = new();
    private readonly LoginThrottle _loginThrottle = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Timer? _healthTimer;
    private DateTime _startTime;

    // ── 页面模板缓存 ──

    private static string? _loginTemplate;
    private static string? _logoutTemplate;
    private static string? _browseTemplate;

    private static string LoadWebTemplate(string name)
    {
        var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", name);
        return File.ReadAllText(path);
    }

    // ── 会话管理 ──

    private class WebDavSession
    {
        public string Username { get; init; } = "";
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    }

    private class SessionManager
    {
        private readonly ConcurrentDictionary<string, WebDavSession> _sessions = new(StringComparer.Ordinal);
        private static readonly TimeSpan Expiry = TimeSpan.FromHours(24);

        public string Create(string username)
        {
            var token = Guid.NewGuid().ToString("N");
            _sessions[token] = new WebDavSession { Username = username };
            Cleanup();
            return token;
        }

        public string? Validate(string? token)
        {
            if (token == null) return null;
            if (_sessions.TryGetValue(token, out var s) && DateTime.UtcNow - s.CreatedAt < Expiry)
                return s.Username;
            if (token != null) _sessions.TryRemove(token, out _);
            return null;
        }

        public void Remove(string? token)
        {
            if (token != null) _sessions.TryRemove(token, out _);
        }

        private void Cleanup()
        {
            var cutoff = DateTime.UtcNow - Expiry;
            foreach (var kvp in _sessions)
                if (kvp.Value.CreatedAt < cutoff)
                    _sessions.TryRemove(kvp.Key, out _);
        }
    }

    // ── 登录失败限速（防在线爆破） ──

    private class LoginThrottle
    {
        private const int MaxFailures = 10;
        private static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);

        private sealed class Entry { public int Count; public DateTime FirstAt; public DateTime LockedUntil; }

        private readonly Dictionary<string, Entry> _map = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public bool IsBlocked(string key)
        {
            lock (_lock)
                return _map.TryGetValue(key, out var e) && e.LockedUntil > DateTime.UtcNow;
        }

        public void Fail(string key)
        {
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                Cleanup(now);
                if (!_map.TryGetValue(key, out var e) || now - e.FirstAt > Window)
                {
                    e = new Entry { FirstAt = now };
                    _map[key] = e;
                }
                if (++e.Count >= MaxFailures)
                {
                    Log.Warning("登录失败过多，临时锁定来源 {Key}（{Minutes} 分钟）", key, Lockout.TotalMinutes);
                    e.LockedUntil = now + Lockout;
                    e.Count = 0;
                    e.FirstAt = now;
                }
            }
        }

        public void Success(string key)
        {
            lock (_lock) _map.Remove(key);
        }

        private void Cleanup(DateTime now)
        {
            if (_map.Count < 1024) return;
            foreach (var key in _map
                         .Where(kv => kv.Value.LockedUntil < now && now - kv.Value.FirstAt > Window)
                         .Select(kv => kv.Key).ToList())
                _map.Remove(key);
        }
    }

    private static readonly XNamespace D = "DAV:";

    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "PUT", "DELETE", "MKCOL", "MOVE", "COPY", "PROPPATCH"
    };

    // 只有这些方法（真正的 WebDAV 客户端会发的）才回应 401 质询头。
    private static readonly HashSet<string> ChallengeMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPTIONS", "PROPFIND", "PUT", "DELETE", "MKCOL", "MOVE", "COPY", "PROPPATCH", "LOCK", "UNLOCK"
    };

    // 浏览器（含 fetch/XHR）收到带 WWW-Authenticate 的 401 会弹原生 Basic 登录框，
    // 而 WebDAV 客户端必须靠质询头才能认证，所以用 Sec-Fetch-* 区分二者：
    // 现代浏览器每个请求都带 Sec-Fetch-*，原生 WebDAV 客户端（资源管理器/rclone/Cyberduck/curl…）都不带。
    private static bool IsBrowserRequest(HttpListenerContext ctx)
        => ctx.Request.Headers["Sec-Fetch-Mode"] != null
        || ctx.Request.Headers["Sec-Fetch-Site"] != null;

    // 请求体上限：登录（JSON/表单）与打包下载各自限制，避免无界读取撑爆内存。
    private const int MaxLoginBodyBytes = 64 * 1024;
    private const int MaxZipBodyBytes = 1024 * 1024;

    public WebDAVServer(WebDAVConfig config)
    {
        _config = config;
        _users = config.GetUserPasswordMap();
        _readonlyMap = config.GetReadOnlyMap();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new HttpListener();
        var bindHost = _config.Host == "0.0.0.0" ? "+" : _config.Host;
        var certHost = (bindHost == "+" || bindHost == "*") ? "localhost" : bindHost;

        // HTTP
        if (_config.EnableHttp)
        {
            var httpPrefix = $"http://{bindHost}:{_config.Port}/";
            _listener.Prefixes.Add(httpPrefix);
            Log.Information("HTTP: {Prefix}", httpPrefix);
        }
        else
        {
            Log.Information("HTTP 未启用");
        }

        // HTTPS
        if (_config.EnableHttps)
        {
            Log.Information("正在初始化 HTTPS (端口: {Port}, 绑定地址: {Host})...", _config.HttpsPort, bindHost);
            var certThumbprint = CertificateHelper.EnsureSelfSignedCert(certHost, _config.HttpsPort);
            if (certThumbprint != null)
            {
                var httpsPrefix = $"https://{bindHost}:{_config.HttpsPort}/";
                _listener.Prefixes.Add(httpsPrefix);
                Log.Information("HTTPS 已就绪: {Prefix} (证书: {Thumbprint})", httpsPrefix, certThumbprint);
                _config.CertThumbprint = certThumbprint;
            }
            else
            {
                Log.Warning("HTTPS 初始化失败，仅启动 {Available}\n" +
                    "  → 请以管理员身份运行 (创建自签名证书和 netsh 绑定需要管理员权限)\n" +
                    "  → 或检查端口 {Port} 是否被占用",
                    _config.EnableHttp ? "HTTP" : "(无监听器)",
                    _config.HttpsPort);
            }
        }
        else
        {
            Log.Information("HTTPS 未启用，仅启动 {Available}",
                _config.EnableHttp ? $"HTTP: http://{bindHost}:{_config.Port}/" : "(无监听器)");
        }

        if (_listener.Prefixes.Count == 0)
        {
            Log.Fatal("HTTP 和 HTTPS 均已禁用，服务无法启动。请启用至少一个协议。");
            return;
        }

        try
        {
            _listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Log.Fatal(ex, "服务启动失败: 端口 {Port}/{HttpsPort} 被占用或 SSL 绑定失败。\n" +
                $"  ① 检查端口 {_config.Port}/{_config.HttpsPort} 是否被其他程序占用\n" +
                "  ② HTTPS 需要管理员权限绑定证书，请确保服务以管理员身份运行\n" +
                "  ③ 尝试在配置中更换端口或关闭 HTTPS");
            return;
        }

        Log.Information("共享目录: {RootDir}", _config.RootDir);

        // 启动健康检查定时器（每 20 分钟）
        _startTime = DateTime.UtcNow;
        _healthTimer = new Timer(_ => HealthCheck(), null, TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20));

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(ctx), _cts.Token);
            }
        }
        catch (HttpListenerException) { }
        catch (OperationCanceledException) { }
    }

    private void HealthCheck()
    {
        try
        {
            var uptime = DateTime.UtcNow - _startTime;
            var dirOk = Directory.Exists(_config.RootDir);
            var httpsOk = !_config.EnableHttps;
            if (_config.EnableHttps && _config.CertThumbprint != null)
            {
                using var store = new System.Security.Cryptography.X509Certificates.X509Store(
                    System.Security.Cryptography.X509Certificates.StoreName.My,
                    System.Security.Cryptography.X509Certificates.StoreLocation.LocalMachine);
                try
                {
                    store.Open(System.Security.Cryptography.X509Certificates.OpenFlags.ReadOnly);
                    var cert = store.Certificates.Find(
                        System.Security.Cryptography.X509Certificates.X509FindType.FindByThumbprint,
                        _config.CertThumbprint, false);
                    httpsOk = cert.Count > 0 && cert[0].NotAfter > DateTimeOffset.UtcNow;
                    store.Close();
                }
                catch { httpsOk = false; }
            }

            Log.Information("[健康检查] 运行中 (运行时间: {Uptime}, " +
                "共享目录: {DirStatus}, " +
                "HTTPS证书: {HttpsStatus})",
                uptime.ToString(@"d\.hh\:mm\:ss"),
                dirOk ? "正常" : "异常 (目录不存在)",
                httpsOk ? "正常" : "异常 (证书过期或丢失)");

            if (!dirOk)
                Log.Warning("共享目录不存在: {RootDir}", _config.RootDir);
            if (_config.EnableHttps && !httpsOk)
                Log.Warning("HTTPS 证书异常，尝试重新绑定...");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "健康检查异常");
        }
    }

    public Task StopAsync()
    {
        _healthTimer?.Dispose();
        _cts?.Cancel();
        _listener?.Stop();
        _listener?.Close();
        // 停止时不删除证书绑定，下次启动还能用
        return Task.CompletedTask;
    }

    // ── 请求分发 ──

    private async Task HandleRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "/";
            var method = ctx.Request.HttpMethod;

            // Web 页面路由
            if (path == "/login")
            {
                await HandleLoginAsync(ctx);
                return;
            }
            if (path == "/logout")
            {
                await HandleLogout(ctx);
                return;
            }
            // 浏览器会自动请求 favicon；直接 204，避免它落到 401 分支触发原生认证框
            if (path == "/favicon.ico")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            // SPA: 浏览器 GET 时提供前端 SPA（JS 自己处理所有 WebDAV 请求）。
            // 根路径无条件提供（登录入口）；其他路径仅在已持有会话、且目标是目录时提供，
            // 未认证一律落到下面的鉴权分支——否则未认证客户端可借 200/401 的差异探测目录是否存在。
            if (method == "GET" && (ctx.Request.Headers["Accept"] ?? "").Contains("text/html"))
            {
                var fullPath = MapPath(GetRelativePath(path));
                if (path == "/" || (fullPath != null && Directory.Exists(fullPath) && GetSessionUser(ctx) != null))
                {
                    await SendBrowsePage(ctx);
                    return;
                }
            }

            // 浏览器 GET → 必须走 Session Cookie；WebDAV 允许 Basic Auth
            var isBrowser = method == "GET" && (ctx.Request.Headers["Accept"] ?? "").Contains("text/html");
            if (!Authenticate(ctx, out var username, isBrowser))
            {
                if (isBrowser)
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.Headers.Add("Location", "/login");
                }
                else
                {
                    ctx.Response.StatusCode = 401;
                    // 只对真正的 WebDAV 客户端发质询头；浏览器（含 fetch/XHR）收到会弹原生登录框
                    var challenged = ChallengeMethods.Contains(method) && !IsBrowserRequest(ctx);
                    if (challenged)
                        ctx.Response.Headers.Add("WWW-Authenticate", "Basic realm=\"WebDAV\"");
                    Log.Information("认证失败: {Method} {Path} (质询: {Challenged})", method, path, challenged);
                }
                ctx.Response.Close();
                return;
            }

            if (WriteMethods.Contains(method) && _readonlyMap.GetValueOrDefault(username, false))
            {
                Log.Warning("拒绝只读用户 [{User}] 的 {Method} 请求: {Path}",
                    username, method, ctx.Request.Url?.AbsolutePath);
                await SendErrorAsync(ctx.Response, 403, $"用户 [{username}] 只有只读权限");
                return;
            }

            Log.Debug("{Method} {Path} (user: {User})", method, ctx.Request.Url?.AbsolutePath, username);

            // POST = 打包下载（SPA 专用，body 为 {"paths":["<绝对URL>",...]}）
            if (method == "POST")
            {
                await HandleZipDownloadAsync(ctx);
                return;
            }

            switch (method)
            {
                case "OPTIONS":   HandleOptions(ctx); break;
                case "PROPFIND":  await HandlePropFindAsync(ctx); break;
                case "GET":       await HandleGetAsync(ctx); break;
                case "HEAD":      HandleHead(ctx); break;
                case "PUT":       await HandlePutAsync(ctx); break;
                case "DELETE":    await HandleDeleteAsync(ctx); break;
                case "MKCOL":     HandleMkCol(ctx); break;
                case "COPY":      await HandleCopyAsync(ctx); break;
                case "MOVE":      await HandleMoveAsync(ctx); break;
                case "PROPPATCH": await HandlePropPatchAsync(ctx); break;
                case "LOCK":      await HandleLockAsync(ctx); break;
                case "UNLOCK":    HandleUnlock(ctx); break;
                default:
                    ctx.Response.StatusCode = 405;
                    ctx.Response.Close();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理请求异常");
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    // ── Basic 认证 ──

    private string? GetSessionUser(HttpListenerContext ctx)
    {
        var token = GetSessionCookie(ctx);
        return token != null ? _sessions.Validate(token) : null;
    }

    /// <summary>手动解析 Cookie 头（HttpListenerRequest.Cookies 有解析 bug）</summary>
    private static string? GetSessionCookie(HttpListenerContext ctx)
    {
        var header = ctx.Request.Headers["Cookie"];
        if (string.IsNullOrEmpty(header)) return null;
        foreach (var part in header.Split(';'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0].Trim().Equals("session", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(kv[1].Trim());
        }
        return null;
    }

    /// <summary>
    /// 浏览器 GET 请求 → 必须通过 Session Cookie 认证，忽略 Basic Auth<br/>
    /// WebDAV 请求 → Session 或 Basic Auth 均可
    /// </summary>
    private bool Authenticate(HttpListenerContext ctx, out string username, bool requireSession)
    {
        username = "";

        // 总是先检查会话 Cookie
        var sessionUser = GetSessionUser(ctx);
        if (sessionUser != null) { username = sessionUser; return true; }

        // 浏览器请求必须走 Cookie，不降级到 Basic Auth（避免浏览器缓存 Basic Auth 自动重登录）
        if (requireSession) return false;

        // WebDAV 客户端 → Basic Auth 降级
        var authHeader = ctx.Request.Headers["Authorization"];
        if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return false;

        var clientKey = ClientKey(ctx);
        if (_loginThrottle.IsBlocked(clientKey))
        {
            Log.Warning("来源已被限速，拒绝认证: {Key} ({Path})", clientKey, ctx.Request.Url?.AbsolutePath);
            return false;
        }

        try
        {
            var encoded = authHeader.Substring(6).Trim();
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            var parts = decoded.Split(':', 2);
            if (parts.Length != 2) return false;
            var user = parts[0];
            var pass = parts[1];

            if (_users.TryGetValue(user, out var expectedPass) && expectedPass == pass)
            {
                _loginThrottle.Success(clientKey);
                username = user;
                return true;
            }
            _loginThrottle.Fail(clientKey);
        }
        catch { }
        return false;
    }

    // ── 登录页面 ──

    private async Task HandleLoginAsync(HttpListenerContext ctx)
    {
        // 已登录则跳回首页
        var existing = GetSessionCookie(ctx);
        if (existing != null && _sessions.Validate(existing) != null)
        {
            ctx.Response.StatusCode = 302;
            ctx.Response.Headers.Add("Location", "/");
            ctx.Response.Close();
            return;
        }

        if (ctx.Request.HttpMethod == "POST")
        {
            // JSON 请求（来自 SPA）
            if ((ctx.Request.ContentType ?? "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.Request.ContentLength64 > MaxLoginBodyBytes)
                {
                    var bigBytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"请求体过大\"}");
                    ctx.Response.StatusCode = 413;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.ContentLength64 = bigBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bigBytes);
                    ctx.Response.Close();
                    return;
                }
                var jsonBody = await ReadBodyLimitedAsync(ctx.Request.InputStream, MaxLoginBodyBytes);
                if (jsonBody == null)
                {
                    var bigBytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"请求体过大\"}");
                    ctx.Response.StatusCode = 413;
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.ContentLength64 = bigBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bigBytes);
                    ctx.Response.Close();
                    return;
                }
                try
                {
                    var json = JsonDocument.Parse(jsonBody);
                    var jsonUser = json.RootElement.GetProperty("username").GetString() ?? "";
                    var jsonPass = json.RootElement.GetProperty("password").GetString() ?? "";

                    var clientKey = ClientKey(ctx);
                    if (_loginThrottle.IsBlocked(clientKey))
                    {
                        var blkBytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"尝试次数过多，请稍后再试\"}");
                        ctx.Response.StatusCode = 429;
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.ContentLength64 = blkBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(blkBytes);
                        ctx.Response.Close();
                        return;
                    }

                    if (_users.TryGetValue(jsonUser, out var jsonExpected) && jsonExpected == jsonPass)
                    {
                        _loginThrottle.Success(clientKey);
                        var token = _sessions.Create(jsonUser);
                        SetSessionCookie(ctx, token);
                        var okBytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.ContentLength64 = okBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(okBytes);
                    }
                    else
                    {
                        _loginThrottle.Fail(clientKey);
                        var errBytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"用户名或密码错误\"}");
                        ctx.Response.ContentType = "application/json; charset=utf-8";
                        ctx.Response.ContentLength64 = errBytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(errBytes);
                    }
                }
                catch
                {
                    var errBytes = Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"请求格式错误\"}");
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.ContentLength64 = errBytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(errBytes);
                }
                ctx.Response.Close();
                return;
            }

            // 表单 POST（浏览器传统登录）
            if (ctx.Request.ContentLength64 > MaxLoginBodyBytes)
            {
                await SendErrorAsync(ctx.Response, 413, "请求体过大");
                return;
            }
            var body = await ReadBodyLimitedAsync(ctx.Request.InputStream, MaxLoginBodyBytes);
            if (body == null)
            {
                await SendErrorAsync(ctx.Response, 413, "请求体过大");
                return;
            }
            var parsed = ParseFormBody(body);
            var user = parsed.GetValueOrDefault("username", "");
            var pass = parsed.GetValueOrDefault("password", "");

            var formKey = ClientKey(ctx);
            if (_loginThrottle.IsBlocked(formKey))
            {
                await SendLoginPage(ctx, true);
                return;
            }

            if (_users.TryGetValue(user, out var expected) && expected == pass)
            {
                _loginThrottle.Success(formKey);
                var token = _sessions.Create(user);
                SetSessionCookie(ctx, token);
                ctx.Response.StatusCode = 302;
                ctx.Response.Headers.Add("Location", "/");
                ctx.Response.Close();
                return;
            }

            _loginThrottle.Fail(formKey);
            await SendLoginPage(ctx, true);
            return;
        }

        await SendLoginPage(ctx, false);
    }

    private async Task HandleLogout(HttpListenerContext ctx)
    {
        var token = GetSessionCookie(ctx);
        _sessions.Remove(token);
        Log.Information("退出登录: Token={Token}", token ?? "(无)");
        ctx.Response.SetCookie(new Cookie("session", "")
        {
            Expires = DateTime.UtcNow.AddDays(-1),
            Path = "/"
        });

        _logoutTemplate ??= LoadWebTemplate("logout.html");
        var html = _logoutTemplate
            .Replace("{{APP_NAME}}", AppInfo.Name)
            .Replace("{{AUTHOR}}", AppInfo.Author)
            .Replace("{{AUTHOR_URL}}", AppInfo.AuthorUrl)
            .Replace("{{VERSION}}", AppInfo.Version);
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.StatusCode = 200;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private async Task SendLoginPage(HttpListenerContext ctx, bool error)
    {
        _loginTemplate ??= LoadWebTemplate("login.html");
        var html = _loginTemplate
            .Replace("{{APP_NAME}}", AppInfo.Name)
            .Replace("{{ERROR_BLOCK}}", error ? "<div class='error'>用户名或密码错误</div>" : "");
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.StatusCode = 200;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static Dictionary<string, string> ParseFormBody(string body)
    {
        var dict = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(body)) return dict;
        foreach (var part in body.Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
                dict[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
        }
        return dict;
    }

    // ── OPTIONS ──

    private void HandleOptions(HttpListenerContext ctx)
    {
        ctx.Response.Headers.Add("Allow", "OPTIONS, GET, HEAD, PROPFIND, PUT, DELETE, MKCOL, COPY, MOVE, PROPPATCH, LOCK, UNLOCK");
        ctx.Response.Headers.Add("DAV", "1, 2");
        ctx.Response.StatusCode = 200;
        ctx.Response.Close();
    }

    // ── PROPFIND ──

    private async Task HandlePropFindAsync(HttpListenerContext ctx)
    {
        var relPath = GetRelativePath(ctx.Request.Url!.AbsolutePath);
        var fullPath = MapOr403(ctx, relPath);
        if (fullPath == null) return;
        var depth = ctx.Request.Headers["Depth"] ?? "1";

        if (!Directory.Exists(fullPath) && !File.Exists(fullPath))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var hrefBase = ctx.Request.Url.AbsolutePath;
        if (!hrefBase.EndsWith("/")) hrefBase += "/";

        var responses = new List<XElement>();
        var isDir = Directory.Exists(fullPath);
        var fsInfo = isDir ? (FileSystemInfo)new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        var selfHref = isDir
            ? hrefBase.TrimEnd('/') + (relPath == "/" || relPath == "" ? "" : "/")
            : hrefBase.TrimEnd('/');

        responses.Add(BuildPropFindResponse(selfHref, fsInfo, isDir));

        if (depth != "0" && isDir)
        {
            try
            {
                var di = (DirectoryInfo)fsInfo;
                foreach (var entry in di.EnumerateFileSystemInfos())
                {
                    var entryIsDir = entry is DirectoryInfo;
                    var entryHref = hrefBase + Uri.EscapeDataString(entry.Name) + (entryIsDir ? "/" : "");
                    responses.Add(BuildPropFindResponse(entryHref, entry, entryIsDir));
                }
            }
            catch (UnauthorizedAccessException) { }
        }

        var multistatus = new XElement(D + "multistatus",
            new XAttribute(XNamespace.Xmlns + "D", D), responses);

        var xmlBytes = Encoding.UTF8.GetBytes(multistatus.ToString(SaveOptions.OmitDuplicateNamespaces));
        ctx.Response.StatusCode = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        ctx.Response.ContentLength64 = xmlBytes.Length;
        await ctx.Response.OutputStream.WriteAsync(xmlBytes);
        ctx.Response.Close();
    }

    private static XElement BuildPropFindResponse(string href, FileSystemInfo fsInfo, bool isDir)
    {
        var lastModified = fsInfo.LastWriteTimeUtc.ToString("R");
        var creationDate = fsInfo.CreationTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");

        var prop = new XElement(D + "prop",
            new XElement(D + "resourcetype", isDir ? new XElement(D + "collection") : null),
            new XElement(D + "displayname", fsInfo.Name),
            new XElement(D + "getlastmodified", lastModified),
            new XElement(D + "creationdate", creationDate),
            isDir ? null : new XElement(D + "getcontentlength", ((FileInfo)fsInfo).Length.ToString()),
            isDir ? null : new XElement(D + "getcontenttype", GetMimeType(fsInfo.Name)),
            new XElement(D + "supportedlock",
                new XElement(D + "lockentry",
                    new XElement(D + "lockscope", new XElement(D + "exclusive")),
                    new XElement(D + "locktype", new XElement(D + "write")))),
            new XElement(D + "lockdiscovery")
        );

        return new XElement(D + "response",
            new XElement(D + "href", href),
            new XElement(D + "propstat", prop, new XElement(D + "status", "HTTP/1.1 200 OK"))
        );
    }

    // ── 打包下载（POST，SPA 专用） ──

    private async Task HandleZipDownloadAsync(HttpListenerContext ctx)
    {
        List<string> urls;
        try
        {
            if (ctx.Request.ContentLength64 > MaxZipBodyBytes)
            {
                await SendErrorAsync(ctx.Response, 413, "请求体过大");
                return;
            }
            var body = await ReadBodyLimitedAsync(ctx.Request.InputStream, MaxZipBodyBytes);
            if (body == null)
            {
                await SendErrorAsync(ctx.Response, 413, "请求体过大");
                return;
            }
            using var doc = JsonDocument.Parse(body);
            urls = doc.RootElement.GetProperty("paths").EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToList();
        }
        catch
        {
            await SendErrorAsync(ctx.Response, 400, "请求格式错误");
            return;
        }

        var targets = new List<(string Full, string Name)>();
        foreach (var u in urls)
        {
            var full = MapOr403(ctx, GetRelativePathFromUrl(u));
            if (full == null) return;
            if (File.Exists(full)) targets.Add((full, Path.GetFileName(full)));
            else if (Directory.Exists(full))
            {
                var name = new DirectoryInfo(full).Name;
                targets.Add((full, string.IsNullOrEmpty(name) ? "download" : name));
            }
        }

        if (targets.Count == 0)
        {
            await SendErrorAsync(ctx.Response, 404, "没有可下载的内容");
            return;
        }

        var firstIsDir = Directory.Exists(targets[0].Full);
        var zipName = targets.Count == 1
            ? (firstIsDir ? targets[0].Name : Path.GetFileNameWithoutExtension(targets[0].Name)) + ".zip"
            : "download.zip";

        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/zip";
        ctx.Response.SendChunked = true;
        ctx.Response.Headers.Add("Content-Disposition",
            $"attachment; filename=\"{Uri.EscapeDataString(zipName)}\"; filename*=UTF-8''{Uri.EscapeDataString(zipName)}");

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var zip = new ZipArchive(ctx.Response.OutputStream, ZipArchiveMode.Create, leaveOpen: true);
            foreach (var (full, name) in targets)
            {
                var top = UniqueEntryName(used, name);
                if (File.Exists(full)) AddFileToZip(zip, full, top);
                else AddDirectoryToZip(zip, full, top);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "打包下载失败: {Path}", ctx.Request.Url?.AbsolutePath);
        }
        ctx.Response.Close();
    }

    private static string UniqueEntryName(HashSet<string> used, string name)
    {
        if (used.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    private static void AddFileToZip(ZipArchive zip, string filePath, string entryName)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var dst = entry.Open();
        using var src = File.OpenRead(filePath);
        src.CopyTo(dst);
    }

    private static void AddDirectoryToZip(ZipArchive zip, string dirPath, string rootName)
    {
        var prefix = rootName + "/";
        var any = false;
        foreach (var file in new DirectoryInfo(dirPath).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            any = true;
            var rel = Path.GetRelativePath(dirPath, file.FullName).Replace('\\', '/');
            AddFileToZip(zip, file.FullName, prefix + rel);
        }
        if (!any) zip.CreateEntry(prefix);
    }

    // ── GET ──

    private async Task HandleGetAsync(HttpListenerContext ctx)
    {
        var fullPath = MapOr403(ctx, GetRelativePath(ctx.Request.Url!.AbsolutePath));
        if (fullPath == null) return;

        if (Directory.Exists(fullPath))
        {
            await SendBrowsePage(ctx);
            return;
        }

        if (!File.Exists(fullPath))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var fi = new FileInfo(fullPath);
        ctx.Response.ContentType = GetMimeType(fi.Name);
        ctx.Response.ContentLength64 = fi.Length;
        ctx.Response.Headers.Add("Last-Modified", fi.LastWriteTimeUtc.ToString("R"));

        await using var fs = fi.OpenRead();
        await fs.CopyToAsync(ctx.Response.OutputStream);
        ctx.Response.Close();
    }

    // ── HEAD ──

    private void HandleHead(HttpListenerContext ctx)
    {
        var fullPath = MapOr403(ctx, GetRelativePath(ctx.Request.Url!.AbsolutePath));
        if (fullPath == null) return;

        if (!File.Exists(fullPath))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        var fi = new FileInfo(fullPath);
        ctx.Response.ContentType = GetMimeType(fi.Name);
        ctx.Response.ContentLength64 = fi.Length;
        ctx.Response.Headers.Add("Last-Modified", fi.LastWriteTimeUtc.ToString("R"));
        ctx.Response.StatusCode = 200;
        ctx.Response.Close();
    }

    // ── PUT ──

    private async Task HandlePutAsync(HttpListenerContext ctx)
    {
        var fullPath = MapOr403(ctx, GetRelativePath(ctx.Request.Url!.AbsolutePath));
        if (fullPath == null) return;
        var parentDir = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(parentDir)) Directory.CreateDirectory(parentDir);

        try
        {
            var existed = File.Exists(fullPath);
            await using var fs = File.Create(fullPath);
            await ctx.Request.InputStream.CopyToAsync(fs);
            ctx.Response.StatusCode = existed ? 204 : 201;
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PUT 写入失败: {Path}", fullPath);
            await SendErrorAsync(ctx.Response, 500, "写入失败");
        }
    }

    // ── DELETE ──

    private async Task HandleDeleteAsync(HttpListenerContext ctx)
    {
        var fullPath = MapOr403(ctx, GetRelativePath(ctx.Request.Url!.AbsolutePath));
        if (fullPath == null) return;

        try
        {
            if (File.Exists(fullPath)) { File.Delete(fullPath); ctx.Response.StatusCode = 204; }
            else if (Directory.Exists(fullPath)) { Directory.Delete(fullPath, true); ctx.Response.StatusCode = 204; }
            else ctx.Response.StatusCode = 404;
        }
        catch (UnauthorizedAccessException) { ctx.Response.StatusCode = 403; }
        catch (Exception ex) { Log.Error(ex, "DELETE 失败"); ctx.Response.StatusCode = 500; }

        ctx.Response.Close();
        await Task.CompletedTask;
    }

    // ── MKCOL ──

    private void HandleMkCol(HttpListenerContext ctx)
    {
        var fullPath = MapOr403(ctx, GetRelativePath(ctx.Request.Url!.AbsolutePath));
        if (fullPath == null) return;

        if (Directory.Exists(fullPath)) ctx.Response.StatusCode = 405;
        else
        {
            try { Directory.CreateDirectory(fullPath); ctx.Response.StatusCode = 201; }
            catch { ctx.Response.StatusCode = 409; }
        }
        ctx.Response.Close();
    }

    // ── COPY ──

    private async Task HandleCopyAsync(HttpListenerContext ctx)
    {
        var destHeader = ctx.Request.Headers["Destination"];
        if (string.IsNullOrEmpty(destHeader)) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }

        var srcFullPath = MapOr403(ctx, GetRelativePath(ctx.Request.Url!.AbsolutePath));
        if (srcFullPath == null) return;
        var destFullPath = MapOr403(ctx, GetRelativePathFromUrl(destHeader));
        if (destFullPath == null) return;
        var overwrite = !string.Equals(ctx.Request.Headers["Overwrite"], "F", StringComparison.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(srcFullPath))
            {
                var destDir = Path.GetDirectoryName(destFullPath)!;
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(srcFullPath, destFullPath, overwrite);
            }
            else if (Directory.Exists(srcFullPath))
            {
                // 目标若落在源目录内部，CopyDirectory 会把刚建出的目标再当子目录递归下去，
                // 直到 StackOverflowException 打崩进程（.NET 中不可捕获）。先拒绝。
                if (IsSameOrUnder(destFullPath, srcFullPath))
                {
                    ctx.Response.StatusCode = 409;   // Conflict
                    ctx.Response.Close();
                    return;
                }
                CopyDirectory(srcFullPath, destFullPath, overwrite);
            }
            else { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

            ctx.Response.StatusCode = overwrite ? 204 : 201;
        }
        catch (Exception ex) { Log.Error(ex, "COPY 失败"); ctx.Response.StatusCode = 500; }

        ctx.Response.Close();
        await Task.CompletedTask;
    }

    // ── MOVE ──

    private async Task HandleMoveAsync(HttpListenerContext ctx)
    {
        var destHeader = ctx.Request.Headers["Destination"];
        if (string.IsNullOrEmpty(destHeader)) { ctx.Response.StatusCode = 400; ctx.Response.Close(); return; }

        var srcFullPath = MapOr403(ctx, GetRelativePath(ctx.Request.Url!.AbsolutePath));
        if (srcFullPath == null) return;
        var destFullPath = MapOr403(ctx, GetRelativePathFromUrl(destHeader));
        if (destFullPath == null) return;

        try
        {
            var destDir = Path.GetDirectoryName(destFullPath)!;
            if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);

            if (File.Exists(srcFullPath)) File.Move(srcFullPath, destFullPath, true);
            else if (Directory.Exists(srcFullPath)) Directory.Move(srcFullPath, destFullPath);
            else { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }

            ctx.Response.StatusCode = 201;
        }
        catch (Exception ex) { Log.Error(ex, "MOVE 失败"); ctx.Response.StatusCode = 500; }

        ctx.Response.Close();
        await Task.CompletedTask;
    }

    // ── PROPPATCH ──

    private async Task HandlePropPatchAsync(HttpListenerContext ctx)
    {
        var href = ctx.Request.Url!.AbsolutePath;
        var response = new XElement(D + "response",
            new XElement(D + "href", href),
            new XElement(D + "propstat",
                new XElement(D + "prop"),
                new XElement(D + "status", "HTTP/1.1 200 OK")));

        var multistatus = new XElement(D + "multistatus", response);
        var xmlBytes = Encoding.UTF8.GetBytes(multistatus.ToString());
        ctx.Response.StatusCode = 207;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        ctx.Response.ContentLength64 = xmlBytes.Length;
        await ctx.Response.OutputStream.WriteAsync(xmlBytes);
        ctx.Response.Close();
    }

    // ── LOCK / UNLOCK ──

    private async Task HandleLockAsync(HttpListenerContext ctx)
    {
        var href = ctx.Request.Url!.AbsolutePath;
        var lockToken = $"urn:uuid:{Guid.NewGuid()}";
        var response = new XElement(D + "prop",
            new XElement(D + "lockdiscovery",
                new XElement(D + "activelock",
                    new XElement(D + "locktype", new XElement(D + "write")),
                    new XElement(D + "lockscope", new XElement(D + "exclusive")),
                    new XElement(D + "depth", "0"),
                    new XElement(D + "locktoken", new XElement(D + "href", lockToken)),
                    new XElement(D + "lockroot", new XElement(D + "href", href)))));

        var xmlBytes = Encoding.UTF8.GetBytes(response.ToString());
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/xml; charset=utf-8";
        ctx.Response.Headers.Add("Lock-Token", $"<{lockToken}>");
        ctx.Response.ContentLength64 = xmlBytes.Length;
        await ctx.Response.OutputStream.WriteAsync(xmlBytes);
        ctx.Response.Close();
    }

    private void HandleUnlock(HttpListenerContext ctx)
    {
        ctx.Response.StatusCode = 204;
        ctx.Response.Close();
    }

    // ── 辅助方法 ──

    private static string GetRelativePath(string urlAbsolutePath)
        => Uri.UnescapeDataString(urlAbsolutePath).TrimStart('/');

    private static string GetRelativePathFromUrl(string fullUrl)
    {
        try { return GetRelativePath(new Uri(fullUrl).AbsolutePath); }
        catch { return fullUrl.TrimStart('/'); }
    }

    private string? MapPath(string relativePath)
    {
        try
        {
            // Path.Combine 遇到「有根」的第二段会直接返回它，故先挡掉盘符/绝对路径/UNC
            if (Path.IsPathRooted(relativePath)) return null;

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_config.RootDir));
            var combined = Path.GetFullPath(
                Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

            // 规范化后必须仍在共享根之下，否则视为目录穿越
            return combined.Equals(root, StringComparison.OrdinalIgnoreCase)
                || combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    ? combined
                    : null;
        }
        catch { return null; }
    }

    /// <summary>映射相对路径；越界则回 403 并返回 null（调用方应 return）。</summary>
    private string? MapOr403(HttpListenerContext ctx, string relativePath)
    {
        var full = MapPath(relativePath);
        if (full == null)
        {
            Log.Warning("拒绝越界路径请求: {Method} {Path}", ctx.Request.HttpMethod, ctx.Request.Url?.AbsolutePath);
            ctx.Response.StatusCode = 403;
            ctx.Response.Close();
        }
        return full;
    }

    /// <summary>限速键：IPv4 用整地址；IPv6 收敛到 /64，防止在 /64 内轮换地址绕过限速。</summary>
    private static string ClientKey(HttpListenerContext ctx)
    {
        var ip = ctx.Request.RemoteEndPoint?.Address;
        if (ip == null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return "v6:" + Convert.ToHexString(ip.GetAddressBytes(), 0, 8);
        return "v4:" + ip;
    }

    /// <summary>写入会话 Cookie：HttpOnly + SameSite=Lax，HTTPS 下追加 Secure。
    /// 手工拼 Set-Cookie，因为 HttpListener 不序列化 Cookie.SameSite。token 为 32 位十六进制，无需转义。</summary>
    private static void SetSessionCookie(HttpListenerContext ctx, string token)
    {
        var secure = ctx.Request.IsSecureConnection ? "; Secure" : "";
        var expires = DateTime.UtcNow.AddDays(1).ToString("R");
        ctx.Response.AppendHeader("Set-Cookie",
            $"session={token}; Path=/; HttpOnly; SameSite=Lax{secure}; Expires={expires}");
    }

    /// <summary>限长读取请求体（按 UTF-8 解码）；超过 maxBytes 返回 null。防止无界读取耗尽内存。</summary>
    private static async Task<string?> ReadBodyLimitedAsync(Stream input, int maxBytes)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int n;
        while ((n = await input.ReadAsync(buf).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + n > maxBytes) return null;
            ms.Write(buf, 0, n);
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task SendErrorAsync(HttpListenerResponse response, int statusCode, string message)
    {
        var body = Encoding.UTF8.GetBytes($"{statusCode} - {message}");
        response.StatusCode = statusCode;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    private static async Task SendBrowsePage(HttpListenerContext ctx)
    {
        _browseTemplate ??= LoadWebTemplate("browse.html");

        var html = _browseTemplate
            .Replace("{{APP_NAME}}", AppInfo.Name)
            .Replace("{{AUTHOR}}", AppInfo.Author)
            .Replace("{{AUTHOR_URL}}", AppInfo.AuthorUrl)
            .Replace("{{VERSION}}", AppInfo.Version);

        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1048576 => $"{bytes / 1024.0:F1} KB",
        < 1073741824 => $"{bytes / 1048576.0:F1} MB",
        _ => $"{bytes / 1073741824.0:F1} GB"
    };

    private static void CopyDirectory(string srcDir, string destDir, bool overwrite)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(srcDir))
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite);
        foreach (var dir in Directory.GetDirectories(srcDir))
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)!), overwrite);
    }

    /// <summary>candidate 是否等于 ancestor 或位于其下（按规范化后的全路径比较）。</summary>
    private static bool IsSameOrUnder(string candidate, string ancestor)
    {
        var a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ancestor));
        var c = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return c.Equals(a, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMimeType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" => "application/javascript",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".pdf" => "application/pdf",
        ".zip" => "application/zip",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".svg" => "image/svg+xml",
        ".mp3" => "audio/mpeg",
        ".mp4" => "video/mp4",
        ".avi" => "video/x-msvideo",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream"
    };
}
