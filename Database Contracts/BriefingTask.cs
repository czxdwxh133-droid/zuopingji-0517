using NewsBriefingAssistant.Enums;

namespace NewsBriefingAssistant.DatabaseContracts;

/// <summary>
/// 一次速览任务记录
/// </summary>
public class BriefingTask : BaseEntity
{
    public string ListPageUrl { get; set; } = string.Empty;
    public NewsBriefingSourceType SourceType { get; set; }
    public NewsBriefingTaskStatus Status { get; set; } = NewsBriefingTaskStatus.Created;
    public int TotalCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public List<BriefingArticleResult> ArticleResults { get; set; } = new();
}
