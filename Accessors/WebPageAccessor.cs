using HtmlAgilityPack;
using Microsoft.Playwright;

namespace NewsBriefingAssistant.Accessors;

/// <summary>
/// 网页抓取器：优先使用 Playwright 渲染 JS 页面，获取完整 DOM 后解析
/// </summary>
public class WebPageAccessor : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<WebPageAccessor> _logger;
    private IBrowser? _browser;
    private readonly SemaphoreSlim _browserLock = new(1, 1);

    public WebPageAccessor(HttpClient httpClient, ILogger<WebPageAccessor> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// 获取 Playwright 浏览器实例（懒加载）
    /// </summary>
    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser != null && _browser.IsConnected)
            return _browser;

        await _browserLock.WaitAsync();
        try
        {
            if (_browser != null && _browser.IsConnected)
                return _browser;

            var playwright = await Playwright.CreateAsync();
            _browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true,
                Args = new[] { "--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage" }
            });
            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    /// <summary>
    /// 页面抓取结果
    /// </summary>
    public record FetchResult(HtmlDocument? Doc, string? ErrorDetail);

    /// <summary>
    /// 获取网页 HTML 文档（多通道自动降级：curl → HttpClient → Playwright）
    /// </summary>
    public async Task<FetchResult> FetchPageAsync(string url, CancellationToken ct = default)
    {
        // ★ 通道 1：curl.exe（绕过 Windows HTTP 栈的 TLS 兼容问题，最可靠）
        var (curlHtml, curlError) = await FetchWithCurlAsync(url, ct);
        if (curlHtml != null)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(curlHtml);
            _logger.LogInformation("curl 获取成功（{Bytes}字节）: {Url}", curlHtml.Length, url);
            return new FetchResult(doc, null);
        }
        _logger.LogWarning("curl 通道失败: {Error}，尝试 HttpClient...", curlError);

        // ★ 通道 2：.NET HttpClient 静态请求
        var (html, staticError) = await FetchStaticAsync(url, ct);

        if (html != null)
        {
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var hasBody = doc.DocumentNode.SelectSingleNode("//body") != null;
            var htmlLen = html.Length;
            
            if (hasBody && htmlLen > 500)
            {
                _logger.LogInformation("HttpClient 静态页面有效（{Bytes}字节）: {Url}", htmlLen, url);
                return new FetchResult(doc, null);
            }
            
            _logger.LogInformation("HttpClient 页面内容可疑（{Bytes}字节，body={HasBody}），尝试 Playwright: {Url}", 
                htmlLen, hasBody, url);
        }

        // ★ 通道 3：Playwright 浏览器渲染
        var (playwrightDoc, playwrightError) = await FetchWithPlaywrightAsync(url, ct);
        if (playwrightDoc != null)
            return new FetchResult(playwrightDoc, null);

        // Playwright 失败，静态 HTML 兜底
        if (html != null)
        {
            _logger.LogInformation("Playwright 失败，回退到静态 HTML（{Bytes}字节）: {Url}", html.Length, url);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            return new FetchResult(doc, null);
        }

        var finalError = $"curl: ({curlError}) → HttpClient: ({staticError}) → Playwright: ({playwrightError})";
        _logger.LogWarning("所有抓取方式均失败: {Url}，原因: {Error}", url, finalError);
        return new FetchResult(null, finalError);
    }

    /// <summary>
    /// 通过 curl.exe 子进程获取网页（绕过 Windows HTTP 栈的 TLS 兼容问题）
    /// </summary>
    private async Task<(string? Html, string? Error)> FetchWithCurlAsync(string url, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "curl.exe",
                Arguments = $"-s -L --max-time 20 -H \"User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36\" -H \"Accept-Language: zh-CN,zh;q=0.9,en;q=0.8\" \"{url}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
                return (null, "无法启动 curl.exe（未安装或不在 PATH 中）");

            var readTask = process.StandardOutput.ReadToEndAsync();
            
            // 等待进程退出，最多 25 秒
            var completed = await Task.Run(() => process.WaitForExit(25000), ct);
            if (!completed)
            {
                try { process.Kill(); } catch { }
                return (null, "curl 进程执行超时（25秒）");
            }

            var html = await readTask;

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(html))
            {
                var stderr = await process.StandardError.ReadToEndAsync();
                return (null, $"curl 返回码={process.ExitCode}，错误: {stderr.Trim()}");
            }

            return (html, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "curl 调用异常: {Url}", url);
            return (null, $"curl 调用异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 静态 HTTP 请求获取页面
    /// </summary>
    private async Task<(string? Html, string? Error)> FetchStaticAsync(string url, CancellationToken ct)
    {
        try
        {
            var response = await _httpClient.GetAsync(url, ct);
            
            if (!response.IsSuccessStatusCode)
            {
                var detail = $"服务器返回 HTTP {(int)response.StatusCode} ({response.ReasonPhrase})";
                _logger.LogWarning("静态请求失败 {Detail}: {Url}", detail, url);
                return (null, detail);
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            return (html, null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("静态请求超时: {Url}", url);
            return (null, "连接超时（服务器无响应或网络不通）");
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("静态请求被取消: {Url}", url);
            return (null, "请求被取消");
        }
        catch (HttpRequestException ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            var diag = "";
            if (detail.Contains("No such host") || detail.Contains("Name or service not known"))
                diag = "DNS解析失败（域名不存在）";
            else if (detail.Contains("timed out") || detail.Contains("timeout"))
                diag = "TCP连接超时（服务器不可达）";
            else if (detail.Contains("certificate"))
                diag = "TLS/SSL证书错误";
            else if (detail.Contains("403"))
                diag = "访问被拒绝（403 Forbidden）";
            else
                diag = $"网络错误：{detail}";
            
            _logger.LogWarning(ex, "静态请求网络错误: {Url} -> {Diag}", url, diag);
            return (null, diag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "静态请求异常: {Url}", url);
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// 使用 Playwright 渲染 JS 页面并获取完整 HTML
    /// </summary>
    private async Task<(HtmlDocument? Doc, string? Error)> FetchWithPlaywrightAsync(string url, CancellationToken ct)
    {
        try
        {
            var browser = await GetBrowserAsync();
            var page = await browser.NewPageAsync();

            try
            {
                await page.SetViewportSizeAsync(1920, 1080);
                await page.GotoAsync(url, new PageGotoOptions
                {
                    // 使用 Load 而非 NetworkIdle，避免被持续的长连接（WebSocket/统计）卡住
                    WaitUntil = WaitUntilState.Load,
                    Timeout = 20000
                });

                // 等待额外的动态内容加载
                await page.WaitForTimeoutAsync(2000);

                var html = await page.ContentAsync();
                var doc = new HtmlDocument();
                doc.LoadHtml(html);
                return (doc, null);
            }
            finally
            {
                await page.CloseAsync();
            }
        }
        catch (Exception ex)
        {
            var diag = ex.Message.Contains("timeout") 
                ? "Playwright渲染超时（页面加载过慢或服务器无响应）" 
                : $"Playwright渲染失败：{ex.Message}";
            _logger.LogWarning(ex, "Playwright 渲染失败: {Url}", url);
            return (null, diag);
        }
    }

    /// <summary>
    /// 提取页面文本内容（去除 HTML 标签）
    /// </summary>
    public string ExtractText(HtmlNode? node)
    {
        if (node == null) return string.Empty;
        return HtmlEntity.DeEntitize(node.InnerText).Trim();
    }

    /// <summary>
    /// 相对 URL 转绝对 URL
    /// </summary>
    public string ResolveUrl(string baseUrl, string relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl)) return string.Empty;

        if (Uri.TryCreate(relativeUrl, UriKind.Absolute, out var absoluteUri))
            return absoluteUri.ToString();

        if (Uri.TryCreate(new Uri(baseUrl), relativeUrl, out var resolvedUri))
            return resolvedUri.ToString();

        return relativeUrl;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser != null)
        {
            await _browser.CloseAsync();
            await _browser.DisposeAsync();
        }
        _browserLock.Dispose();
        GC.SuppressFinalize(this);
    }
}
