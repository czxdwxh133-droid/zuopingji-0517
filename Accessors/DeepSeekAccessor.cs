using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace NewsBriefingAssistant.Accessors;

/// <summary>
/// DeepSeek API 配置
/// </summary>
public class DeepSeekOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string Model { get; set; } = "deepseek-chat";
}

/// <summary>
/// DeepSeek API 访问器：调用大模型生成新闻摘要
/// </summary>
public class DeepSeekAccessor
{
    private readonly HttpClient _httpClient;
    private readonly DeepSeekOptions _options;
    private readonly ILogger<DeepSeekAccessor> _logger;

    public DeepSeekAccessor(HttpClient httpClient, IOptions<DeepSeekOptions> options, ILogger<DeepSeekAccessor> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// [Agent 模式] 多轮对话：发送消息历史 + 工具定义，支持 function calling
    /// </summary>
    public async Task<DeepSeekResponse?> ChatWithToolsAsync(
        List<AgentChatMessage> messages, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogWarning("未配置 API Key，Agent 模式不可用");
            return null;
        }

        var requestBody = new
        {
            model = _options.Model,
            messages,
            tools = Engines.AgentEngine.ToolDefinitions,
            temperature = 0.3,
            max_tokens = 4096
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
        {
            Content = content
        };
        request.Headers.Add("Authorization", $"Bearer {_options.ApiKey}");

        var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DeepSeekResponse>(responseBody);
    }

    /// <summary>
    /// [传统模式] 对单篇文章生成摘要（保留向后兼容）
    /// </summary>
    public async Task<(string Title, string Summary, string KeyPoints, string ImportanceNote)> SummarizeArticleAsync(
        string articleTitle, string articleBody, int maxLength = 2000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(articleBody))
            return (articleTitle, string.Empty, string.Empty, "内容为空");

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogInformation("未配置 DeepSeek API Key，使用清洗后的原文截取作为降级摘要");
            var cleanedBody = CleanArticleBody(articleBody);
            var fallbackSummary = Truncate(cleanedBody, 300);
            return (articleTitle, fallbackSummary, string.Empty, string.Empty);
        }

        var prompt = BuildSummarizationPrompt(articleTitle, Truncate(articleBody, maxLength));

        var requestBody = new
        {
            model = _options.Model,
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = prompt }
            },
            temperature = 0.3,
            max_tokens = 1024,
            stream = false
        };

        try
        {
            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl.TrimEnd('/')}/chat/completions")
            {
                Content = content
            };
            request.Headers.Add("Authorization", $"Bearer {_options.ApiKey}");

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStringAsync(ct);
            var result = JsonSerializer.Deserialize<DeepSeekResponse>(responseBody);

            var rawOutput = result?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
            return ParseStructuredOutput(rawOutput, articleTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeepSeek API 调用失败");
            return (articleTitle, string.Empty, string.Empty, "AI 摘要生成失败");
        }
    }

    private static string BuildSummarizationPrompt(string title, string body)
    {
        return $"""
            请对以下新闻文章进行精炼总结，仅返回以下格式：

            【标题】{title}

            【摘要】控制在 300 字以内，精炼概括文章核心内容。

            以下是文章正文：
            {body}
            """;
    }

    private static (string Title, string Summary, string KeyPoints, string ImportanceNote) ParseStructuredOutput(
        string rawOutput, string fallbackTitle)
    {
        var title = fallbackTitle;
        var summary = string.Empty;

        if (string.IsNullOrWhiteSpace(rawOutput))
            return (title, summary, string.Empty, string.Empty);

        // 按【摘要】分割
        var parts = rawOutput.Split(new[] { "【摘要】" }, StringSplitOptions.None);

        if (parts.Length >= 2)
        {
            summary = parts[1].Trim();
            // 截断到 300 字
            if (summary.Length > 300)
                summary = summary[..300];
        }

        // 尝试从输出中提取标题
        var titleSection = parts[0].Replace("【标题】", "").Trim();
        if (!string.IsNullOrWhiteSpace(titleSection))
            title = titleSection;

        return (title, summary, string.Empty, string.Empty);
    }

    private static string Truncate(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength) return text;
        return text[..maxLength] + "...";
    }

    /// <summary>
    /// 清洗文章正文：去除面包屑导航、来源信息、责任编辑、版权声明等无关内容
    /// </summary>
    private static string CleanArticleBody(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        // 去掉面包屑导航（如 "首页 > 智能时代>人工智能"）
        var cleaned = System.Text.RegularExpressions.Regex.Replace(raw,
            @"^(首页\s*[>＞»]\s*)+[\s\S]*?(?=\S{10,})", "");

        // 去掉来源信息行（如 "来源：IT之家 作者：浩渺 责编：浩渺"）
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"来源[：:]\s*\S+.*?(?=\S{10,})", " ");

        // 去掉"作者：""责编：""评论："等元信息
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"(作者|责编|编辑|记者)[：:]\s*\S+", " ");

        // 去掉"感谢xxx网友xxx的线索投递"
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"感谢[\s\S]*?线索投递[！!]?", " ");

        // 去掉日期时间模式（如 "2026/7/16 22:06:04"）
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"\d{4}[/-]\d{1,2}[/-]\d{1,2}\s+\d{1,2}:\d{2}(:\d{2})?", " ");

        // 去掉"IT之家 X 月 X 日消息"这类导语
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"IT之家\s*\d+\s*月\s*\d+\s*日消息[，,]", " ");

        // 去掉评论数等
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned,
            @"评论[：:]\s*\d+", " ");

        // 规范化空白
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();

        return cleaned;
    }

    private const string SystemPrompt = """
        你是一个专业的新闻速览助手。你的任务是对新闻文章进行精炼总结，帮助用户在最短时间内判断文章是否值得阅读。
        请严格按照指定格式输出，保持客观、准确、简洁。使用中文回复。
        """;
}

// ---- JSON 模型 ----

/// <summary>DeepSeek API 响应</summary>
public class DeepSeekResponse
{
    [JsonPropertyName("choices")]
    public List<DeepSeekChoice>? Choices { get; set; }
}

public class DeepSeekChoice
{
    [JsonPropertyName("message")]
    public DeepSeekMessage? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

/// <summary>API 返回的消息（支持 tool_calls）</summary>
public class DeepSeekMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<DeepSeekToolCall>? ToolCalls { get; set; }
}

public class DeepSeekToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("function")]
    public DeepSeekFunctionCall Function { get; set; } = new();
}

public class DeepSeekFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

// ---- Agent 对话消息模型（用于构建请求） ----

/// <summary>Agent 多轮对话中的一条消息</summary>
public class AgentChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AgentToolCall>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

public class AgentToolCall
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public AgentFunctionCall Function { get; set; } = new();
}

public class AgentFunctionCall
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}
