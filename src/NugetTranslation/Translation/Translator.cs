using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Serilog;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NugetTranslation.Translation;

/// <summary>翻译器。DI 单例，持有 ChatClient 和目标语言代码，为每个 member 独立创建 AI 会话进行翻译。</summary>
internal sealed class Translator
{
    private readonly OpenAI.Chat.ChatClient _chatClient;
    private readonly string _language;

    /// <summary>最近一次翻译的 token 用量，从 ParseResult.Usage 透传。</summary>
    public UsageDetails? LastUsage { get; private set; }

    public Translator(OpenAI.Chat.ChatClient chatClient, string language)
    {
        _chatClient = chatClient;
        _language = language;
    }

    /// <summary>翻译一段 member XML。内部创建 agent 会话，成功返回翻译后的 XElement。</summary>
    /// <param name="memberXml">原始 member 的 XML 字符串</param>
    /// <param name="readme">可选的 Readme 上下文</param>
    /// <param name="ct">取消令牌</param>
    /// <returns>翻译后的 XElement</returns>
    public async Task<XElement> TranslateMemberAsync(
        string memberXml, string? readme, CancellationToken ct = default)
    {
        var original = XElement.Parse(memberXml);
        var result = new ParseResult(original);

        // —— 创建 agent ——
        var tool = AIFunctionFactory.Create(
            (string translatedText) => result.TrySet(translatedText),
            name: "ValidateTranslation",
            description: "验证 AI 的翻译结果是否符合 XML 格式，name 属性是否正确");

        var agent = new ChatClientAgent(
            _chatClient.AsIChatClient(),
            instructions: $$"""
                你是一个翻译器。目标语言代码：{{_language}}。
                你仅翻译 XML、JSON 等配置文件中的文本内容（如 summary、param、returns 等标签内的文字），
                不翻译格式标签、属性名、属性值中的标识符。
                你必须调用 ValidateTranslation 工具，把你的翻译内容作为参数。
                工具会验证和使用你的翻译格式。你不需要输出对话内容。
                """,
            name: "Translator",
            tools: [tool]);

        // 中间件：检测 ParseResult.Translated 是否被设置。
        // TrySet 返回 null（验证通过）→ Translated 非 null → ctx.Terminate 终止对话。
        // TrySet 返回 string（验证失败）→ AI 在工具结果中看到错误 → 自行重试翻译。
        var builder = new AIAgentBuilder(agent);
        builder.Use(async (inner, ctx, next, ct2) =>
        {
            var agentResponse = await next(ctx, ct2);
            if (result.Translated is not null)
                ctx.Terminate = true;
            return agentResponse;
        });
        var built = builder.Build();

        // —— 构造会话 ——
        var session = await built.CreateSessionAsync(ct);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(readme))
        {
            messages.Add(new ChatMessage(ChatRole.User,
                [new TextContent(
                    $"这是包的 Readme，帮助了解上下文：\n{readme}\n\n这是你要翻译的内容：\n{memberXml}")]));
        }
        else
        {
            messages.Add(new ChatMessage(ChatRole.User,
                [new TextContent($"这是你要翻译的内容：\n{memberXml}")]));
        }

        // —— 单次翻译（agent 内部自动处理 AI→tool→AI 重试） ——
        var chatCompletion = await built.RunAsync(messages, session, null, ct);

        // 取 token 用量
        if (chatCompletion?.Usage is { } tokenUsage)
        {
            result.Usage = LastUsage = new UsageDetails
            {
                InputTokenCount = tokenUsage.InputTokenCount,
                OutputTokenCount = tokenUsage.OutputTokenCount,
                TotalTokenCount = tokenUsage.TotalTokenCount
            };
        }

        return result.Translated
            ?? throw new InvalidOperationException("翻译失败：未获取到有效结果");
    }

    /// <summary>解析并验证 AI 翻译结果。TrySet 返回 null 表示成功，返回 string 表示错误消息。</summary>
    internal sealed class ParseResult
    {
        private static readonly Regex FenceRegex = new(
            @"(?<=```xml\s*).*?(?=\s*```)", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>原始 member 的 XElement</summary>
        public XElement Original { get; }

        /// <summary>翻译成功后设置的结果</summary>
        public XElement? Translated { get; private set; }

        /// <summary>本次翻译的 token 用量</summary>
        public UsageDetails? Usage { get; set; }

        public ParseResult(XElement original)
        {
            Original = original;
        }

        /// <summary>验证并设置翻译结果。成功返回 null，失败返回错误消息字符串。</summary>
        public string? TrySet([Description("AI 生成的翻译后 XML 文本")] string raw)
        {
            // 去除 markdown fence（断言正则，直接取 Value）
            var match = FenceRegex.Match(raw);
            if (match.Success)
                raw = match.Value;
            raw = raw.Trim();

            // 解析 XML
            XElement? parsed;
            try { parsed = XElement.Parse(raw); }
            catch (Exception ex)
            {
                return $"XML 解析失败：{ex.Message}。请确保输出是完整的 XML 格式。";
            }

            // 验证 name 属性与原 member 一致
            var expected = Original.Attribute("name")?.Value;
            var actual = parsed.Attribute("name")?.Value;
            if (expected != actual)
            {
                return $"name 属性不匹配。期望：{expected}，实际：{actual}。请保持 name 属性不变。";
            }

            Translated = parsed;
            return null;
        }
    }
}
