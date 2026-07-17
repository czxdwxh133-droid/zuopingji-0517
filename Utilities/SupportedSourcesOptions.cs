namespace NewsBriefingAssistant.Utilities;

/// <summary>
/// 支持的新闻源配置
/// </summary>
public class SupportedSourcesOptions
{
    public List<NewsSourceConfig> Sources { get; set; } = new();
}

public class NewsSourceConfig
{
    public string Name { get; set; } = string.Empty;
    public string DomainPattern { get; set; } = string.Empty;
    public string ArticleLinkSelector { get; set; } = string.Empty;
    public string TitleSelector { get; set; } = string.Empty;
    public string ContentSelector { get; set; } = string.Empty;
    public int MaxArticles { get; set; } = 20;
    /// <summary>是否允许提取外站链接（聚合类站点如猫目需要设为 true）</summary>
    public bool AllowExternalLinks { get; set; } = false;
}
