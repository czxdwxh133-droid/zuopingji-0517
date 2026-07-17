namespace NewsBriefingAssistant.LogicContracts;

/// <summary>
/// 从新闻列表页识别出的文章候选条目
/// </summary>
public class NewsArticleCandidate
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int Index { get; set; }
}
