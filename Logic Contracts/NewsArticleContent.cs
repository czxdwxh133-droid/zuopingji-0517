namespace NewsBriefingAssistant.LogicContracts;

/// <summary>
/// 从文章页面提取出的完整内容
/// </summary>
public class NewsArticleContent
{
    public string Title { get; set; } = string.Empty;
    public string BodyText { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public DateTime? PublishDate { get; set; }
}
