using System.Threading.Channels;
using NewsBriefingAssistant.Engines;
using NewsBriefingAssistant.Enums;
using NewsBriefingAssistant.LogicContracts;

namespace NewsBriefingAssistant.Managers;

/// <summary>
/// 进度事件：用于逐条向 UI 推送处理进度
/// </summary>
public class BriefingProgressEvent
{
    public BriefingProgressType Type { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public BriefingResult? Result { get; set; }
    public NewsBriefingTaskStatus? TaskStatus { get; set; }
    public bool IsFinished { get; set; }
}

public enum BriefingProgressType
{
    Info,
    Started,
    ArticleCandidate,
    ArticleProcessing,
    ArticleCompleted,
    ArticleFailed,
    TaskCompleted,
    Error,
    SourceNotSupported
}

/// <summary>
/// 新闻速览管理器：委托给 AgentEngine，由 LLM 自主决策完成速览
/// </summary>
public class NewsBriefingManager
{
    private readonly AgentEngine _agent;
    private readonly ILogger<NewsBriefingManager> _logger;

    public NewsBriefingManager(AgentEngine agent, ILogger<NewsBriefingManager> logger)
    {
        _agent = agent;
        _logger = logger;
    }

    /// <summary>
    /// 执行速览任务（统一模式）：Agent 自行判断 URL 是列表页还是单篇文章
    /// </summary>
    public async Task ExecuteUnifiedAsync(
        string url,
        Channel<BriefingProgressEvent> channel,
        CancellationToken ct = default)
    {
        _logger.LogInformation("启动 Agent（统一模式）: {Url}", url);
        await _agent.RunUnifiedAsync(url, channel, ct);
    }
}
