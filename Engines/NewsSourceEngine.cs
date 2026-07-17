using System.Text.RegularExpressions;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NewsBriefingAssistant.Accessors;
using NewsBriefingAssistant.Enums;
using NewsBriefingAssistant.LogicContracts;
using NewsBriefingAssistant.Utilities;

namespace NewsBriefingAssistant.Engines;

/// <summary>
/// 新闻源引擎：负责站点识别、列表页文章链接提取、文章内容抽取
/// </summary>
public class NewsSourceEngine
{
    private readonly WebPageAccessor _webPageAccessor;
    private readonly List<NewsSourceConfig> _sources;
    private readonly ILogger<NewsSourceEngine> _logger;

    public NewsSourceEngine(
        WebPageAccessor webPageAccessor,
        IOptions<SupportedSourcesOptions> options,
        ILogger<NewsSourceEngine> logger)
    {
        _webPageAccessor = webPageAccessor;
        _sources = options.Value.Sources;
        _logger = logger;
    }

    /// <summary>
    /// 判断 URL 是否属于当前支持的新闻站点，返回匹配的源配置
    /// </summary>
    public (bool IsSupported, NewsSourceConfig? Config, NewsBriefingSourceType SourceType) ValidateSource(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return (false, null, NewsBriefingSourceType.Unknown);

        foreach (var source in _sources)
        {
            if (url.Contains(source.DomainPattern, StringComparison.OrdinalIgnoreCase))
            {
                var sourceType = MapToSourceType(source);
                return (true, source, sourceType);
            }
        }

        return (false, null, NewsBriefingSourceType.Unknown);
    }

    /// <summary>
    /// 从列表页提取新闻文章链接，带诊断信息
    /// </summary>
    public async Task<(List<NewsArticleCandidate> Candidates, string? DiagInfo)> ExtractArticleCandidatesAsync(
        string listPageUrl, NewsSourceConfig config, CancellationToken ct = default)
    {
        var candidates = new List<NewsArticleCandidate>();
        string? diagInfo = null;

        var fetchResult = await _webPageAccessor.FetchPageAsync(listPageUrl, ct);
        var doc = fetchResult.Doc;

        if (doc == null)
        {
            _logger.LogWarning("无法获取列表页: {Url}，原因: {Error}", listPageUrl, fetchResult.ErrorDetail);
            return (candidates, fetchResult.ErrorDetail ?? "页面无法获取（网络错误或网站拒绝访问）");
        }

        // ★ 策略 1：Nuxt/SSR 页面 → 直接从 __NUXT_DATA__ JSON 提取
        var nuxtCount = TryExtractNuxtArticles(doc, listPageUrl, candidates, config);
        if (nuxtCount > 0)
        {
            _logger.LogInformation("Nuxt SSR 提取到 {Count} 篇文章，跳过选择器", nuxtCount);
            return (candidates, $"Nuxt SSR 数据提取成功，共 {nuxtCount} 篇");
        }

        var selectors = config.ArticleLinkSelector.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matchedSelectors = new List<string>();

        foreach (var selector in selectors)
        {
            try
            {
                var before = candidates.Count;
                ExtractCandidatesWithXPath(doc, selector.Trim(), candidates, config, listPageUrl, ref seenUrls);
                if (candidates.Count > before) matchedSelectors.Add(selector.Trim());
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "选择器 {Selector} 未匹配到结果", selector);
            }
        }

        // 配置选择器无结果时，自动尝试通用降级选择器
        if (candidates.Count == 0)
        {
            _logger.LogInformation("配置选择器无结果，尝试通用降级选择器...");
            foreach (var fallback in FallbackSelectors)
            {
                try
                {
                    var before = candidates.Count;
                    ExtractCandidatesWithXPath(doc, fallback, candidates, config, listPageUrl, ref seenUrls);
                    if (candidates.Count > before)
                    {
                        matchedSelectors.Add($"降级:{fallback}");
                        _logger.LogInformation("降级选择器 {Selector} 提取到 {Count} 篇文章", fallback, candidates.Count);
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "降级选择器 {Selector} 失败", fallback);
                }
            }
        }

        // 终极兜底：所有选择器都失败，直接找所有 <a> 标签
        if (candidates.Count == 0)
        {
            _logger.LogInformation("所有选择器无结果，使用终极兜底：扫描全部 <a> 标签...");
            var allLinks = doc.DocumentNode.SelectNodes("//a[@href]");
            if (allLinks != null)
            {
                var linkCount = allLinks.Count;
                foreach (var link in allLinks)
                {
                    if (candidates.Count >= config.MaxArticles) break;

                    var href = link.GetAttributeValue("href", string.Empty);
                    if (string.IsNullOrWhiteSpace(href)) continue;

                    var absoluteUrl = _webPageAccessor.ResolveUrl(listPageUrl, href);

                    if (!IsLikelyArticleUrl(absoluteUrl, config)) continue;

                    var cleanUrl = absoluteUrl.Split('#')[0];
                    if (!seenUrls.Add(cleanUrl)) continue;

                    var title = _webPageAccessor.ExtractText(link).Trim();
                    if (string.IsNullOrWhiteSpace(title) || title.Length < 6) continue;

                    candidates.Add(new NewsArticleCandidate
                    {
                        Title = title,
                        Url = cleanUrl,
                        Index = candidates.Count + 1
                    });
                }
                diagInfo = $"页面共{linkCount}个链接，结构化选择器均未命中，兜底扫描后提取{ candidates.Count}篇";
            }
            else
            {
                diagInfo = "页面HTML中未找到任何 <a> 链接，可能是纯JS渲染页面";
            }
        }

        _logger.LogInformation("从 {Url} 提取到 {Count} 篇候选文章", listPageUrl, candidates.Count);
        return (candidates, diagInfo);
    }

    /// <summary>通用降级选择器：覆盖常见新闻列表结构</summary>
    private static readonly string[] FallbackSelectors =
    {
        "//li//a[h3]",        // 列表项中的链接含 h3 子元素
        "//a[h3]",            // 任意含 h3 子元素的链接
        "//h3/a",             // h3 内的直接链接
        "//h3//a",            // h3 内的任意层级链接
        "//h2/a",             // h2 内的直接链接
        "//h2//a",            // h2 内的任意层级链接
        "//article//a[h1]",   // article 内含 h1 的链接
        "//article//a[h2]",   // article 内含 h2 的链接
        "//article//a[h3]",   // article 内含 h3 的链接
        "//article//h3/a",    // article 内 h3 的直接链接
        "a.article-link",     // class="article-link" 的链接
        "a.title",            // class="title" 的链接
        "//div[contains(@class,'article')]//a[h3]",  // article 容器内的链接
        "//div[contains(@class,'post')]//a[h3]",     // post 容器内的链接
        "//div[contains(@class,'news')]//a",         // news 容器内的任意链接
        "//section//a[h3]",   // section 内的 h3 链接
    };

    private void ExtractCandidatesWithXPath(HtmlAgilityPack.HtmlDocument doc, string selector,
        List<NewsArticleCandidate> candidates, NewsSourceConfig config,
        string baseUrl, ref HashSet<string> seenUrls)
    {
        var xpath = CssToXPath(selector);
        var nodes = doc.DocumentNode.SelectNodes(xpath);
        if (nodes == null) return;

        foreach (var node in nodes)
        {
            if (candidates.Count >= config.MaxArticles) break;

            var href = node.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(href)) continue;

            var absoluteUrl = _webPageAccessor.ResolveUrl(baseUrl, href);

            if (!IsLikelyArticleUrl(absoluteUrl, config)) continue;

            var cleanUrl = absoluteUrl.Split('#')[0];

            if (!seenUrls.Add(cleanUrl)) continue;

            var title = _webPageAccessor.ExtractText(node).Trim();
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (config.AllowExternalLinks && title.Length < 6) continue;

            candidates.Add(new NewsArticleCandidate
            {
                Title = title,
                Url = cleanUrl,
                Index = candidates.Count + 1
            });
        }
    }

    /// <summary>
    /// 从文章页面提取标题和正文内容（含通用降级策略）
    /// </summary>
    public async Task<NewsArticleContent?> ExtractArticleContentAsync(
        string articleUrl, NewsSourceConfig config, CancellationToken ct = default)
    {
        var fetchResult = await _webPageAccessor.FetchPageAsync(articleUrl, ct);
        var doc = fetchResult.Doc;
        if (doc == null) return null;

        // 提取标题：先尝试配置的选择器，再尝试通用降级
        var title = ExtractBySelector(doc, config.TitleSelector, articleUrl);
        if (string.IsNullOrWhiteSpace(title))
        {
            // 降级：尝试 <title> 标签或任意 <h1>
            title = ExtractTitleFallback(doc);
        }

        // 提取正文：先尝试配置的选择器
        var bodyText = ExtractBySelector(doc, config.ContentSelector, articleUrl);
        if (string.IsNullOrWhiteSpace(bodyText))
        {
            // 降级：提取 <body> 中最大文本块
            bodyText = ExtractBodyFallback(doc);
        }

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(bodyText))
            return null;

        return new NewsArticleContent
        {
            Title = title,
            BodyText = CleanBodyText(bodyText),
            SourceName = config.Name
        };
    }

    /// <summary>标题提取降级：<title> 标签 → 第一个 h1 → 页面 title 属性</summary>
    private static string ExtractTitleFallback(HtmlAgilityPack.HtmlDocument doc)
    {
        // 尝试 <title> 标签
        var titleNode = doc.DocumentNode.SelectSingleNode("//title");
        if (titleNode != null)
        {
            var title = titleNode.InnerText.Trim();
            // 去除站点名后缀（如 " - IT之家" " | 36氪"）
            var separators = new[] { " - ", " | ", " _ ", " — ", " – " };
            foreach (var sep in separators)
            {
                var idx = title.IndexOf(sep);
                if (idx > 0)
                {
                    title = title.Substring(0, idx).Trim();
                    break;
                }
            }
            if (title.Length > 5) return title;
        }

        // 尝试第一个 h1
        var h1Node = doc.DocumentNode.SelectSingleNode("//h1");
        if (h1Node != null)
        {
            var h1Text = HtmlAgilityPack.HtmlEntity.DeEntitize(h1Node.InnerText).Trim();
            if (h1Text.Length > 5) return h1Text;
        }

        return string.Empty;
    }

    /// <summary>正文提取降级：找页面中最大的文本块（通常是正文）</summary>
    private static string ExtractBodyFallback(HtmlAgilityPack.HtmlDocument doc)
    {
        // 移除脚本、样式、导航等干扰元素
        var body = doc.DocumentNode.SelectSingleNode("//body");
        if (body == null) return string.Empty;

        var skipTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "script", "style", "nav", "header", "footer", "noscript", "iframe", "svg" };

        var candidates = new List<(string Text, int Length)>();

        // 遍历所有 div/section/article/p 块级元素，找到文本最长的几个
        foreach (var tag in new[] { "//article", "//div", "//section", "//p" })
        {
            var nodes = doc.DocumentNode.SelectNodes(tag);
            if (nodes == null) continue;

            foreach (var node in nodes)
            {
                // 跳过干扰标签内的节点
                var ancestor = node.ParentNode;
                var skip = false;
                while (ancestor != null)
                {
                    if (skipTags.Contains(ancestor.Name))
                    {
                        skip = true;
                        break;
                    }
                    ancestor = ancestor.ParentNode;
                }
                if (skip) continue;

                var text = HtmlAgilityPack.HtmlEntity.DeEntitize(node.InnerText).Trim();
                if (text.Length > 100)
                {
                    candidates.Add((text, text.Length));
                }
            }
        }

        // 找到最长文本块（通常就是正文）
        if (candidates.Count > 0)
        {
            candidates.Sort((a, b) => b.Length.CompareTo(a.Length));
            return candidates[0].Text;
        }

        // 最后兜底：整个 body 文本
        var bodyText = HtmlAgilityPack.HtmlEntity.DeEntitize(body.InnerText).Trim();
        return bodyText.Length > 200 ? bodyText : string.Empty;
    }

    // ---- 私有辅助方法 ----

    private string ExtractBySelector(HtmlAgilityPack.HtmlDocument doc, string selectorCsv, string fallbackUrl)
    {
        var selectors = selectorCsv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var selector in selectors)
        {
            try
            {
                var xpath = CssToXPath(selector.Trim());
                var node = doc.DocumentNode.SelectSingleNode(xpath);
                if (node != null)
                {
                    var text = _webPageAccessor.ExtractText(node);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }
            catch { /* skip invalid selectors */ }
        }

        return string.Empty;
    }

    private static string CleanBodyText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // 规范化空白字符
        var cleaned = Regex.Replace(raw, @"\s+", " ");
        // 移除多余换行
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
    }

    private static bool IsLikelyArticleUrl(string url, NewsSourceConfig config)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        var lower = url.ToLowerInvariant();

        // 域名检查：聚合类站点（如猫目）允许外链
        if (!config.AllowExternalLinks)
        {
            if (!lower.Contains(config.DomainPattern.ToLowerInvariant())) return false;
        }

        // 排除明显的非文章页面
        var excludePatterns = new[]
        {
            "/index", "/about", "/help", "/login", "/register",
            "/search", "javascript:", "mailto:", "#", "/tag/",
            "/video", "/photo", "/live", "/special/",
            "/categories", "/appstore", "/discover",
        };

        foreach (var pattern in excludePatterns)
        {
            if (lower.Contains(pattern)) return false;
        }

        // URL 路径必须有一定深度
        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath.Trim('/');
            if (string.IsNullOrEmpty(path)) return false;
            // 聚合站链接可能较浅（如 /article/123），放宽到 ≥1 层
            return path.Split('/').Length >= 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将 CSS 选择器转换为 HtmlAgilityPack 可用的 XPath 表达式
    /// </summary>
    private static string CssToXPath(string css)
    {
        css = css.Trim();
        if (string.IsNullOrEmpty(css)) return css;
        // 已经是 XPath 则直接返回
        if (css.StartsWith("//") || css.StartsWith("/")) return css;

        // a[href*='/doc-'] → //a[contains(@href,'/doc-')]
        var attrSubMatch = Regex.Match(css, @"^(\w+)\[(\w+)\*='([^']+)'\]$");
        if (attrSubMatch.Success)
        {
            var tag = attrSubMatch.Groups[1].Value;
            var attr = attrSubMatch.Groups[2].Value;
            var val = attrSubMatch.Groups[3].Value;
            return $"//{tag}[contains(@{attr},'{val}')]";
        }

        // h1.main-title → //h1[contains(@class,'main-title')]
        var classMatch = Regex.Match(css, @"^(\w+)\.([\w_-]+)$");
        if (classMatch.Success)
        {
            var tag = classMatch.Groups[1].Value;
            var cls = classMatch.Groups[2].Value;
            return $"//{tag}[contains(@class,'{cls}')]";
        }

        // div#main → //div[@id='main']
        var idMatch = Regex.Match(css, @"^(\w+)#([\w_-]+)$");
        if (idMatch.Success)
        {
            var tag = idMatch.Groups[1].Value;
            var id = idMatch.Groups[2].Value;
            return $"//{tag}[@id='{id}']";
        }

        // .article-content → //*[contains(@class,'article-content')]
        var classOnly = Regex.Match(css, @"^\.([\w_-]+)$");
        if (classOnly.Success)
        {
            var cls = classOnly.Groups[1].Value;
            return $"//*[contains(@class,'{cls}')]";
        }

        // #main → //*[@id='main']
        var idOnly = Regex.Match(css, @"^#([\w_-]+)$");
        if (idOnly.Success)
        {
            var id = idOnly.Groups[1].Value;
            return $"//*[@id='{id}']";
        }

        // 纯标签名 → //tag
        if (Regex.IsMatch(css, @"^[a-zA-Z][\w]*$"))
            return $"//{css}";

        return css;
    }

    private static NewsBriefingSourceType MapToSourceType(NewsSourceConfig config)
    {
        return config.Name switch
        {
            "新浪新闻" => NewsBriefingSourceType.SinaNews,
            "腾讯新闻" => NewsBriefingSourceType.TencentNews,
            "澎湃新闻" => NewsBriefingSourceType.ThePaper,
            "网易新闻" => NewsBriefingSourceType.NetEaseNews,
            "环球网" => NewsBriefingSourceType.Huanqiu,
            "猫目" => NewsBriefingSourceType.Maomu,
            _ => NewsBriefingSourceType.Unknown
        };
    }

    // ---- Nuxt SSR JSON 数据提取 ----

    /// <summary>
    /// 尝试从 Nuxt SSR 页面内嵌的 __NUXT_DATA__ JSON 中提取文章列表
    /// 返回提取到的文章数量
    /// </summary>
    private int TryExtractNuxtArticles(HtmlAgilityPack.HtmlDocument doc, string baseUrl,
        List<NewsArticleCandidate> candidates, NewsSourceConfig config)
    {
        try
        {
            // 查找 __NUXT_DATA__ script 标签
            var scriptNode = doc.DocumentNode.SelectSingleNode("//script[@id='__NUXT_DATA__']");
            if (scriptNode == null)
                return 0;

            var json = scriptNode.InnerText.Trim();
            if (json.Length < 100)
                return 0;

            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;

            // Nuxt3 格式: [state, data, serverRendered, errors, path]
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2)
                return 0;

            var dataElement = root[1]; // 页面 data 对象

            // 递归搜索 data 对象，找包含 title + sourceLink/link/url 的条目
            var articles = new List<(string Title, string Url)>();
            FindNuxtArticles(dataElement, baseUrl, articles, depth: 0);

            // 去重并添加到候选列表
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (title, url) in articles)
            {
                if (candidates.Count >= config.MaxArticles)
                    break;
                if (string.IsNullOrWhiteSpace(title) || title.Length < 6)
                    continue;
                if (string.IsNullOrWhiteSpace(url))
                    continue;
                if (!seen.Add(url))
                    continue;
                if (!IsLikelyArticleUrl(url, config))
                    continue;

                candidates.Add(new NewsArticleCandidate
                {
                    Title = title,
                    Url = url,
                    Index = candidates.Count + 1
                });
            }

            return candidates.Count;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Nuxt JSON 解析失败（正常情况，说明页面非Nuxt/结构不同）");
            return 0;
        }
    }

    /// <summary>
    /// 递归搜索 Nuxt JSON 树，收集包含 title + 链接字段的条目
    /// </summary>
    private void FindNuxtArticles(JsonElement element, string baseUrl,
        List<(string Title, string Url)> results, int depth)
    {
        // 防止过深递归
        if (depth > 20)
            return;

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                // 数组元素如果是 object 且有 title 字段，检查是否是文章条目
                if (item.ValueKind == JsonValueKind.Object)
                {
                    string? title = null, url = null;

                    foreach (var prop in item.EnumerateObject())
                    {
                        if (prop.Name == "title" && prop.Value.ValueKind == JsonValueKind.String)
                            title = prop.Value.GetString()?.Trim();
                        if ((prop.Name == "sourceLink" || prop.Name == "url" || prop.Name == "link" || prop.Name == "href")
                            && prop.Value.ValueKind == JsonValueKind.String)
                            url = prop.Value.GetString()?.Trim();
                    }

                    if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(url))
                    {
                        var absoluteUrl = _webPageAccessor.ResolveUrl(baseUrl, url);
                        results.Add((title, absoluteUrl));
                    }
                    else
                    {
                        // 不是文章条目，继续递归搜索内部
                        FindNuxtArticles(item, baseUrl, results, depth + 1);
                    }
                }
                else
                {
                    FindNuxtArticles(item, baseUrl, results, depth + 1);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                FindNuxtArticles(prop.Value, baseUrl, results, depth + 1);
            }
        }
    }
}
