using System.Text.Json;
using System.Threading.Channels;
using NewsBriefingAssistant.Accessors;
using NewsBriefingAssistant.LogicContracts;
using NewsBriefingAssistant.Managers;
using NewsBriefingAssistant.Utilities;

namespace NewsBriefingAssistant.Engines;

/// <summary>
/// Agent 引擎：使用 ReAct 循环让 LLM 通过 function calling 自主完成新闻速览
/// LLM 从"被调用工具"升级为"自主决策者"
/// </summary>
public class AgentEngine
{
    private readonly DeepSeekAccessor _deepSeek;
    private readonly NewsSourceEngine _newsSource;
    private readonly ILogger<AgentEngine> _logger;

    private NewsSourceConfig? _config;
    private int _articleCount;
    private int _processedCount;
    private bool _finished;
    private bool _isSingleMode;

    public AgentEngine(DeepSeekAccessor deepSeek, NewsSourceEngine newsSource, ILogger<AgentEngine> logger)
    {
        _deepSeek = deepSeek;
        _newsSource = newsSource;
        _logger = logger;
    }

    /// <summary>
    /// [统一模式] Agent 自行判断 URL 是列表页还是单篇文章，自主选择处理策略
    /// </summary>
    public async Task RunUnifiedAsync(string url, Channel<BriefingProgressEvent> channel, CancellationToken ct)
    {
        _config = null;
        _articleCount = 0;
        _processedCount = 0;
        _finished = false;
        _isSingleMode = false;

        var messages = new List<AgentChatMessage>
        {
            new() { Role = "system", Content = SystemPromptUnified },
            new() { Role = "user", Content = $"请分析以下 URL 并决定处理方式：{url}" }
        };

        await RunReActLoopAsync(messages, channel, ct);
    }

    /// <summary>
    /// 共享的 ReAct 循环核心：最多 50 轮，LLM 自主调用工具
    /// </summary>
    private async Task RunReActLoopAsync(
        List<AgentChatMessage> messages, Channel<BriefingProgressEvent> channel, CancellationToken ct)
    {
        try
        {
            for (int turn = 0; turn < 50 && !ct.IsCancellationRequested; turn++)
            {
                _logger.LogInformation("Agent turn {Turn}...", turn + 1);

                var response = await _deepSeek.ChatWithToolsAsync(messages, ct);
                var msg = response?.Choices?.FirstOrDefault()?.Message;

                if (msg == null)
                {
                    _logger.LogWarning("Agent turn {Turn}: 空响应，结束循环", turn + 1);
                    break;
                }

                messages.Add(new AgentChatMessage
                {
                    Role = "assistant",
                    Content = msg.Content,
                    ToolCalls = msg.ToolCalls?.Select(tc => new AgentToolCall
                    {
                        Id = tc.Id,
                        Type = tc.Type,
                        Function = new AgentFunctionCall
                        {
                            Name = tc.Function.Name,
                            Arguments = tc.Function.Arguments
                        }
                    }).ToList()
                });

                if (msg.ToolCalls is { Count: > 0 })
                {
                    foreach (var tc in msg.ToolCalls)
                    {
                        var result = await ExecuteTool(tc.Function.Name, tc.Function.Arguments, channel, ct);
                        messages.Add(new AgentChatMessage
                        {
                            Role = "tool",
                            ToolCallId = tc.Id,
                            Content = result
                        });
                    }
                }
                else if (!string.IsNullOrWhiteSpace(msg.Content))
                {
                    _logger.LogInformation("Agent 思考: {Content}",
                        msg.Content.Length > 200 ? msg.Content[..200] + "..." : msg.Content);
                }

                if (_finished) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent 执行失败");
            try
            {
                await channel.Writer.WriteAsync(new BriefingProgressEvent
                {
                    Type = BriefingProgressType.Error,
                    Message = $"Agent 执行出错：{ex.Message}",
                    IsFinished = true
                }, ct);
            }
            catch { }
        }
        finally
        {
            if (!_finished)
            {
                try
                {
                    await channel.Writer.WriteAsync(new BriefingProgressEvent
                    {
                        Type = BriefingProgressType.TaskCompleted,
                        Message = _isSingleMode
                            ? "单篇文章处理完毕"
                            : $"任务完成：成功 {_processedCount}/{_articleCount} 篇",
                        Current = _articleCount,
                        Total = _articleCount,
                        IsFinished = true
                    }, ct);
                }
                catch { }
            }
            channel.Writer.Complete();
        }
    }

    // ==================== 工具执行 ====================

    private async Task<string> ExecuteTool(string name, string arguments,
        Channel<BriefingProgressEvent> channel, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            var root = doc.RootElement;

            return name switch
            {
                "get_article_list" => await GetArticleList(root, channel, ct),
                "get_article_content" => await GetArticleContent(root, ct),
                "save_summary" => await SaveSummary(root, channel),
                "finish" => await Finish(root, channel, ct),
                _ => JsonSerializer.Serialize(new { error = $"未知工具: {name}" })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "工具 {Name} 执行失败", name);
            return JsonSerializer.Serialize(new { error = ex.Message });
        }
    }

    private async Task<string> GetArticleList(JsonElement args, Channel<BriefingProgressEvent> channel, CancellationToken ct)
    {
        var url = args.GetProperty("url").GetString()!;

        var (isSupported, config, _) = _newsSource.ValidateSource(url);
        if (!isSupported || config == null)
        {
            _isSingleMode = true; // 无法识别为列表页，通知 Agent 走单篇路径
            return JsonSerializer.Serialize(new { error = "这不是一个支持的新闻列表页，请尝试用 get_article_content 直接获取该URL" });
        }

        _config = config;

        await channel.Writer.WriteAsync(new BriefingProgressEvent
        {
            Type = BriefingProgressType.Info,
            Message = $"Agent 正在分析 {config.Name} 的文章列表..."
        }, ct);

        var (candidates, diagInfo) = await _newsSource.ExtractArticleCandidatesAsync(url, config, ct);
        _articleCount = candidates.Count;

        await channel.Writer.WriteAsync(new BriefingProgressEvent
        {
            Type = BriefingProgressType.Started,
            Message = candidates.Count > 0
                ? $"共 {candidates.Count} 篇文章，Agent 开始逐篇处理..."
                : $"未提取到文章链接（{diagInfo ?? "未知原因"}），请尝试用 get_article_content 直接获取该URL",
            Current = 0,
            Total = candidates.Count
        }, ct);

        var articles = candidates.Select(c => new
        {
            index = c.Index,
            title = c.Title,
            url = c.Url
        }).ToList();

        // 如果没提取到文章，返回明确的错误提示给 LLM
        if (candidates.Count == 0)
        {
            return JsonSerializer.Serialize(new
            {
                count = 0,
                articles = Array.Empty<object>(),
                error = $"未能从该页面提取到文章链接。（{diagInfo}）请换用 get_article_content(url=\"{url}\", index=1) 直接抓取该URL的内容。"
            });
        }

        return JsonSerializer.Serialize(new { count = articles.Count, articles });
    }

    private async Task<string> GetArticleContent(JsonElement args, CancellationToken ct)
    {
        var url = args.GetProperty("url").GetString()!;
        var index = args.TryGetProperty("index", out var idx) ? idx.GetInt32() : 0;

        // 确保有可用的 news source config
        var config = _config;
        if (config == null)
        {
            // 单篇模式：尝试从 URL 验证来源，失败则用通用选择器
            var (isSupported, matched, _) = _newsSource.ValidateSource(url);
            config = isSupported && matched != null
                ? matched
                : new NewsSourceConfig
                {
                    Name = "通用来源",
                    TitleSelector = "h1",
                    ContentSelector = "article, body",
                    DomainPattern = new Uri(url).Host,
                    MaxArticles = 1,
                    ArticleLinkSelector = "",
                    AllowExternalLinks = true
                };
            _config = config;
        }

        var content = await _newsSource.ExtractArticleContentAsync(url, config, ct);

        if (content == null || string.IsNullOrWhiteSpace(content.BodyText))
            return JsonSerializer.Serialize(new { error = "文章内容为空或无法访问", index, url });

        var body = content.BodyText.Length > 2000 ? content.BodyText[..2000] : content.BodyText;

        _logger.LogInformation("Agent 获取文章 #{Index}: {Title}", index, content.Title);

        return JsonSerializer.Serialize(new
        {
            index,
            title = content.Title,
            body,
            source = content.SourceName,
            url
        });
    }

    private async Task<string> SaveSummary(JsonElement args, Channel<BriefingProgressEvent> channel)
    {
        var index = args.GetProperty("index").GetInt32();
        var title = args.GetProperty("title").GetString()!;
        var summary = args.GetProperty("summary").GetString()!;
        var url = args.GetProperty("url").GetString()!;

        if (summary.Length > 300) summary = summary[..300];

        _processedCount++;

        await channel.Writer.WriteAsync(new BriefingProgressEvent
        {
            Type = BriefingProgressType.ArticleProcessing,
            Message = $"Agent 处理中 ({index}/{_articleCount}): {title}",
            Current = index,
            Total = _articleCount
        });

        await channel.Writer.WriteAsync(new BriefingProgressEvent
        {
            Type = BriefingProgressType.ArticleCompleted,
            Message = $"✓ Agent 完成 ({index}/{_articleCount}): {title}",
            Current = index,
            Total = _articleCount,
            Result = new BriefingResult
            {
                OriginalUrl = url,
                Title = title,
                Summary = summary,
                SourceName = _config?.Name ?? "",
                IsSuccess = true,
                Index = index
            }
        });

        _logger.LogInformation("Agent 保存摘要 #{Index}: {Title}", index, title);
        return JsonSerializer.Serialize(new { saved = true, index });
    }

    private async Task<string> Finish(JsonElement args, Channel<BriefingProgressEvent> channel, CancellationToken ct)
    {
        _finished = true;
        var message = args.TryGetProperty("message", out var m) ? m.GetString()! : "所有文章处理完毕";

        await channel.Writer.WriteAsync(new BriefingProgressEvent
        {
            Type = BriefingProgressType.TaskCompleted,
            Message = $"Agent 报告：{message}（共处理 {_processedCount}/{_articleCount} 篇）",
            Current = _articleCount,
            Total = _articleCount,
            IsFinished = true
        }, ct);

        return JsonSerializer.Serialize(new { finished = true });
    }

    // ==================== System Prompt ====================

    private const string SystemPromptUnified = """
        你是一个智能资讯助手 Agent。你的任务是分析用户提供的 URL，自行判断是列表页还是单篇文章，然后采取对应的处理策略。

        ## 可用工具
        - get_article_list: 尝试从 URL 提取文章列表（仅对支持的新闻源有效）
        - get_article_content: 获取单篇文章的标题和正文内容
        - save_summary: 保存一篇摘要结果
        - finish: 结束任务

        ## 决策流程（两步判断，务必遵守）
        1. 第一步：调用 get_article_list(url=用户提供的URL)
        2. 第二步，根据返回结果选择路径：
           【路径A - 列表模式】如果返回了文章列表（count >= 1）：
              a. 按序号依次处理每篇文章
              b. 对每篇：调用 get_article_content → 阅读正文 → 生成300字以内摘要 → 调用 save_summary 保存
              c. 全部完成后调用 finish
           【路径B - 单篇模式】如果 get_article_list 返回错误或 count=0：
              a. 直接对用户原始URL调用 get_article_content(url=原始URL, index=1)
              b. 仔细阅读正文，生成300字以内的精炼摘要
              c. 调用 save_summary(index=1, title=原标题, summary=你的摘要, url=原始URL) 保存
              d. 调用 finish

        ## 注意事项
        - 摘要要客观准确、简洁精炼，严格控制在300字以内
        - 不要自行编造内容，严格基于文章正文生成
        - 优先尝试路径A，失败了自动切换到路径B
        """;

    // ==================== Tool Definitions ====================

    public static readonly object[] ToolDefinitions =
    [
        new
        {
            type = "function",
            function = new
            {
                name = "get_article_list",
                description = "从新闻列表页提取所有文章链接和标题，返回文章列表",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        url = new { type = "string", description = "新闻列表页的完整URL" }
                    },
                    required = new[] { "url" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "get_article_content",
                description = "获取一篇文章的标题和正文内容（用于后续生成摘要）",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        url = new { type = "string", description = "文章详情页URL" },
                        index = new { type = "integer", description = "文章在列表中的序号" }
                    },
                    required = new[] { "url", "index" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "save_summary",
                description = "保存一篇已生成摘要的文章结果",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        index = new { type = "integer", description = "文章序号" },
                        title = new { type = "string", description = "文章标题" },
                        summary = new { type = "string", description = "文章摘要，控制在300字以内" },
                        url = new { type = "string", description = "文章原文链接" }
                    },
                    required = new[] { "index", "title", "summary", "url" }
                }
            }
        },
        new
        {
            type = "function",
            function = new
            {
                name = "finish",
                description = "所有文章处理完毕，结束速览任务",
                parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        message = new { type = "string", description = "完成总结信息" }
                    },
                    required = new[] { "message" }
                }
            }
        }
    ];
}
