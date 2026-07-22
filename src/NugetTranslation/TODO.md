# NugetTranslation 代码可读性重构 TODO

本清单基于子 AI 对全部源文件的审查结论整理，仅涉及**可读性、理解成本、结构清晰度**，不改变任何功能行为。

---

## 1. ConfigLoader：取消两步模式

### 现状

```csharp
internal static class ConfigLoader
{
    public static IReadOnlyDictionary<string, ProfileConfig?>? Instance { get; private set; }

    public static void Load()
    {
        // 读文件、反序列化、赋值给 Instance
    }
}
```

调用方必须写：

```csharp
ConfigLoader.Load();                         // 先 Load
var config = ConfigLoader.Instance;          // 再读 Instance
// 还要判空
```

**问题**：两步顺序靠约定不靠编译器。读者看到 `Instance` 时不知道它有没有被初始化，必须回溯确认 `Load()` 已被调用。忘记调 `Load()` 就是静默 null。先 Load 再 Instance 的契约完全隐式。

### 改为

```csharp
internal static class ConfigLoader
{
    public static Dictionary<string, ProfileConfig?>? Load()
    {
        // 读文件、反序列化、直接返回
    }
}
```

调用方变成一步：

```csharp
var config = ConfigLoader.Load();
if (config is null) { /* 处理 */ }
```

没有 `Instance` 属性，没有 `Load()` + 再读的两步顺序依赖。结果用完即弃，不留静态状态。

### 改动范围

| 文件 | 改动 |
|------|------|
| `Configuration/ConfigLoader.cs` | 删 `Instance` 属性；`Load()` 改为返回 `Dictionary<string, ProfileConfig?>?` |
| `Program.cs` | 删 `ConfigLoader.Load()` 独立调用；所有读配置的地方改为 `var config = ConfigLoader.Load()` |
| `Commands/Tree/Translate/TranslateCommand.cs` | `Handle` 中的 `ConfigLoader.Instance` 改为 `ConfigLoader.Load()` |
| `Commands/Tree/Cache/Build/CacheBuildCommand.cs` | 如果有引用 `Instance` 则改 |
| `Commands/Tree/Cache/Forget/CacheForgetCommand.cs` | 同上 |

---

## 2. MemberCache：消除 ct 遮蔽 + 传递外部取消 + 改名

### 现状

```csharp
public async Task<string> GetOrTranslateAsync(string memberXml, CancellationToken ct = default)
{
    return await _cache.GetOrSetAsync(memberXml, ct => _ai.TranslateAsync(memberXml, ct), token: CancellationToken.None);
}
```

**问题一：`ct` 参数名遮蔽**

lambda 的参数也叫 `ct`，和外层方法的 `CancellationToken ct` 同名。读者读到 `ct => ... ct` 时必须停下来确认：前一个 `ct` 是 lambda 形参（来自 FusionCache 的 factory），后一个 `ct` 也是 lambda 形参。两个 `ct` 都不是外层那个。但如果不仔细看，会以为外层 `ct` 被传进了 `TranslateAsync`。

**问题二：`token: CancellationToken.None` 忽略外部取消**

`GetOrSetAsync` 的 `token` 参数传了 `CancellationToken.None`，完全忽略调用者传入的 `ct`。整个方法的 `CancellationToken` 形参形同虚设。如果上层代码依赖取消功能，这是一个正确性 bug。

**问题三：`TryGetKnown` 命名**

`TryGetKnown` — "Known" 是什么意思？读者要猜测：是 "尝试获取已知项"？"已知的翻译"？实际作用是检查 key 是否存在，按 .NET 惯例应该叫 `Exists` 或 `ContainsKey`。

### 改为

```csharp
public bool Exists(string memberXml)
    => _cache.GetOrDefault<string>(memberXml) is not null;

public async Task<string> GetOrTranslateAsync(string memberXml, CancellationToken ct = default)
{
    return await _cache.GetOrSetAsync(
        memberXml,
        cancelTok => _ai.TranslateAsync(memberXml, cancelTok),
        token: ct);
}
```

| 改了什么 | 为什么 |
|----------|--------|
| `cancelTok` | 消除同名遮蔽，读者一眼区分 lambda 参数和外部参数 |
| `token: ct` | 传递外部取消令牌，上层可取消 |
| `TryGetKnown` → `Exists` | 符合 .NET 惯例，不需猜测含义 |

### 改动范围

| 文件 | 改动 |
|------|------|
| `Translation/MemberCache.cs` | 改方法名、改参数名、改 `token` |
| `Translation/PackageProcessor.cs` | 调用 `Exists` 的地方改名 |
| `Translation/PackageProcessor.cs` | `GetOrTranslateAsync` 调用处确认 `ct` 传递 |
| `Translation/XmlTranslator.cs` | 如果有引用 `MemberCache` 则改 |

---

## 3. AiTranslator：提取 FunctionResult 提取逻辑

### 现状

```csharp
var result = response.Messages
    .SelectMany(m => m.Contents ?? [])
    .OfType<FunctionResultContent>()
    .LastOrDefault()?.Result as string;
```

**问题**：这条 4 层链（Messages → Contents → FunctionResultContent → Result）没有注释。第一次接触 Agent Framework 的读者需要逐层推理每一层filter 了什么、为什么取最后一个。阅读时要在脑子里过一遍 AIAgent 的响应结构才能理解这段代码在做什么。

### 改为

```csharp
public async Task<string> TranslateAsync(string memberXml, CancellationToken ct = default)
{
    var session = await _agent.CreateSessionAsync(ct);
    var response = await _agent.RunAsync(
        [new ChatMessage(ChatRole.User, memberXml)], session, null, ct);
    var result = ExtractFunctionResult(response.Messages);
    return result ?? throw new InvalidOperationException("翻译未返回结果");
}

/// <summary>
/// 从 AI Agent 的响应消息列表中提取最后一个函数调用的返回值。
/// </summary>
/// <remarks>
/// 映射逻辑：
///   Messages → Contents（每条消息的内容集合）
///   Contents → 筛选 FunctionResultContent（工具调用的结果）
///   FunctionResultContent.Result → 返回的 object，转换为 string
/// </remarks>
private static string? ExtractFunctionResult(IList<ChatMessage> messages)
{
    return messages?
        .SelectMany(m => m.Contents ?? [])
        .OfType<FunctionResultContent>()
        .LastOrDefault()?.Result as string;
}
```

| 改了什么 | 为什么 |
|----------|--------|
| 提取 `ExtractFunctionResult` 私有方法 | 职责单一化，主流程更短 |
| 加 `<remarks>` 注释 | 说明每一层映射的含义，读者不用自己推理 |
| 方法名自解释 | "从消息中提取函数结果" 比裸 LINQ 链更直白 |

---

## 4. AgentFactory：工具函数分离

### 现状

`AgentFactory.Create` 约 80 行。其中工具函数 `TranslateMember` 的完整实现——system prompt 构造、`TopP` 参数、`CompleteChatAsync` 调用、`StripFence`、`ParseOrThrow`、name 校验、单次重试——全部内联在 `AIFunctionFactory.Create` 的 lambda 里。

**问题**：`Create` 的命名暗示它只做"组装 Agent"。但读者看到的却是工具函数的全部实现细节。要理解 `Create` 做了什么，必须同时消化两层职责：Agent 的构建流程和翻译工具的业务逻辑。

### 改为

```csharp
public static AIAgent Create(OpenAI.Chat.ChatClient chatClient, string language)
{
    IChatClient iclient = chatClient.AsIChatClient();

    var tool = AIFunctionFactory.Create(
        (string memberXml) => TranslateMemberImpl(chatClient, language, memberXml),
        name: "TranslateMember");

    var agent = new ChatClientAgent(iclient, instructions: $$"""
            你是一个c# xml文档注释翻译器。
            目标语言：{{language}}
            收到内容后调 TranslateMember 工具进行翻译，保持 xml 格式和 name 属性不变。
            """,
        name: "Translator",
        tools: [tool]);

    var builder = new AIAgentBuilder(agent);
    builder.Use(async (inner, ctx, next, ct) =>
    {
        try { var result = await next(ctx, ct); ctx.Terminate = true; return result; }
        catch (Exception ex) { Log.Logger.Debug("工具调用异常: {Msg}", ex.Message); throw; }
    });

    return builder.Build();
}

private static async Task<string> TranslateMemberImpl(
    OpenAI.Chat.ChatClient chatClient, string language, string memberXml)
{
    // 完整的 system prompt、TopP、API 调用、StripFence、校验、重试
    // 原封不动搬运过来
}
```

| 改了什么 | 为什么 |
|----------|--------|
| 工具函数提出为 `TranslateMemberImpl` | `Create` 只表达"用了什么工具"，不表达"工具怎么实现" |
| `Create` 保留 4 个步骤 | 实例化 IChatClient → 创建工具 → 创建 Agent → 挂中间件 |
| 实现细节在私有方法里 | 想改翻译逻辑找 `TranslateMemberImpl`，想改组装逻辑找 `Create` |

---

## 5. PackageProcessor：提取 TranslateMissingAsync + 调整 Stats 位置

### 问题一：翻译缺失循环干扰流程阅读

`ProcessAsync` 约 60 行，6 个步骤用注释分隔。其中第 5 步"翻译缺失"内联了一个 foreach 循环，包含 try-catch 和两条日志。读者第一次看 `ProcessAsync` 时，必须跳过这个循环体才能看到第 6 步。如果只想了解整体流程，循环体内的细节是噪音。

### 改为

```csharp
public async Task ProcessAsync(string packageId, string versionSpec)
{
    // 1. 解析版本
    // 2. 下载 + 解压
    // 3. 预检
    // 4. 统计日志
    // 5. 翻译缺失
    if (stats.MissingKeys.Count > 0)
        await TranslateMissingAsync(packageId, stats.MissingKeys);
    // 6. 组装输出
    await AssembleOutputAsync(packageId, xmlFiles);
}

private async Task TranslateMissingAsync(string packageId, HashSet<string> missingKeys)
{
    int done = 0, err = 0;
    foreach (var key in missingKeys)
    {
        try
        {
            await _memberCache.GetOrTranslateAsync(key);
            done++;
            Log.Logger.Information("{Pkg} 进度: {Done}/{Total}", packageId, done, missingKeys.Count);
        }
        catch (Exception ex)
        {
            err++;
            Log.Logger.Error("{Pkg} 成员翻译失败: {Error}", packageId, ex.Message);
        }
    }
    Log.Logger.Information("{Pkg} 翻译完成: 成功 {Done}，失败 {Err}", packageId, done, err);
}
```

### 问题二：PackageTranslationStats 远离使用点

`PackageTranslationStats` 是 `private sealed class` 定义在文件尾部（约第 200 行），但 `PreCheckAsync`（约第 150 行）返回它。读者在第 150 行看到 `PackageTranslationStats` 时，必须翻到文件底部才知道它有哪几个字段。

### 改为

把 `PackageTranslationStats` 的定义移到 `PreCheckAsync` 方法之前，作为类中第一个私有类型。让读者在读到返回类型时已经见过它的定义。

---

## 6. XmlValidator：改名 + 统一 XML 解析方式

### 现状

类名 `XmlValidator`，但方法集合不只是验证，还包括解析（`TryParse`/`ParseOrThrow`）、围栏清理（`StripFence`）、属性提取（`ExtractName`/`NameMatches`）。类名 `Validator` 暗示只做检查，不涉及转换或提取。

另外，`ExtractName` 用 `Regex.Match` 从字符串中提取 name 属性，而其他方法都用 `XElement` 解析。风格不一致——读者会问：为什么这个操作不走 XML 解析器？

### 改为

类名改 `XmlParser`，`ExtractName` 改用 `XElement`：

```csharp
internal static class XmlParser
{
    private static readonly Regex FenceRegex = new(...);

    public static XElement? TryParse(string text) { ... }
    public static XElement ParseOrThrow(string text) { ... }
    public static bool NameMatches(XElement element, string expected) { ... }
    public static string StripFence(string text) { ... }

    public static string ExtractName(string memberXml)
    {
        var el = TryParse(memberXml);
        return (string?)el?.Attribute("name") ?? "";
    }
}
```

### 改动范围

| 文件 | 改动 |
|------|------|
| `Translation/XmlValidator.cs` | 类名改 `XmlParser`，`ExtractName` 改用 `XElement` |
| `Translation/AgentFactory.cs` | `XmlValidator.` → `XmlParser.` |
| `Translation/PackageProcessor.cs` | 同上 |

---

## 执行顺序

```
1 → ConfigLoader（无依赖，先改）
2 → MemberCache（依赖 ConfigLoader？否）
3 → AiTranslator（无依赖）
4 → AgentFactory（依赖 XmlParser，等第 6 步完成）
5 → PackageProcessor（依赖 MemberCache，等第 2 步完成）
6 → XmlValidator（被 4 依赖，放前面改或一起改）
```

实际上 1、2、3、6 互不依赖，可以先改。4 依赖 6。5 依赖 2。
