namespace NewsBriefingAssistant.LogicContracts;

/// <summary>
/// 速览处理结果：包含原文链接、标题、精炼摘要、关键信息点、重要性说明
/// </summary>
public class BriefingResult
{
    public string OriginalUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string KeyPoints { get; set; } = string.Empty;
    public string ImportanceNote { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public int Index { get; set; }
}
