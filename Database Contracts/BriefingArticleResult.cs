using NewsBriefingAssistant.Enums;

namespace NewsBriefingAssistant.DatabaseContracts;

/// <summary>
/// 单篇文章的速览处理结果
/// </summary>
public class BriefingArticleResult : BaseEntity
{
    public Guid BriefingTaskId { get; set; }
    public BriefingTask? BriefingTask { get; set; }
    public string OriginalUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? KeyPoints { get; set; }
    public string? ImportanceNote { get; set; }
    public string? SourceName { get; set; }
    public NewsBriefingArticleStatus Status { get; set; } = NewsBriefingArticleStatus.Pending;
    public string? ErrorMessage { get; set; }
    public int ArticleIndex { get; set; }
}
