using Microsoft.Extensions.DependencyInjection;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using OpenAI;
using OpenAI.Chat;
using Polly;
using Serilog;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace NugetTranslation;

public static class Translator
{
    public static async Task Run(string packageId, string packageVersion, string language)
    {
        // 启用 OpenAI SDK 遥测
        AppContext.SetSwitch("OpenAI.Experimental.EnableOpenTelemetry", true);
        using var openAiListener = new ActivityListener();
        openAiListener.ShouldListenTo = source => source.Name.StartsWith("OpenAI.");
        openAiListener.Sample = (ref ActivityCreationOptions<ActivityContext> options) => ActivitySamplingResult.AllData;
        openAiListener.ActivityStopped = activity =>
        {
            Log.Logger.Debug("[OpenAI] {Name}，耗时 {Duration:F2}ms，标签: {@Tags}",
                activity.DisplayName, activity.Duration.TotalMilliseconds,
                activity.Tags?.ToDictionary(t => t.Key, t => t.Value));

            foreach (var evt in activity.Events ?? [])
            {
                Log.Logger.Verbose("[OpenAI] 事件: {Name} @ {Time:F1}ms，标签: {@Tags}",
                    evt.Name, (evt.Timestamp - activity.StartTimeUtc).TotalMilliseconds,
                    evt.Tags?.ToDictionary(t => t.Key, t => t.Value));
            }
        };
        ActivitySource.AddActivityListener(openAiListener);

        var build = new ServiceCollection();
        build.AddChatClient();
        var cacheDir = Path.GetFullPath(Path.Combine("..", "cache", language));
        Directory.CreateDirectory(cacheDir);
        build.AddFusionCacheAndSqliteCache(Path.Combine(cacheDir, packageId.ToLower() + ".db"));
        build.AddSourceRepository();
        build.AddSingleton<SourceCacheContext>();
        build.AddSingleton(NullLogger.Instance);
        build.AddPolly();

        using var serviceProvider = build.BuildServiceProvider();

        Log.Logger = new LoggerConfiguration()
                   .Enrich.FromLogContext()
                   .WriteTo.Console()
                   .MinimumLevel.Information()
                   .CreateLogger();


        #region 查找指定Nuget包

        var repository = serviceProvider.GetRequiredService<SourceRepository>();
        var logger = serviceProvider.GetRequiredService<NuGet.Common.ILogger>();
        using var cacheContext = serviceProvider.GetRequiredService<SourceCacheContext>();


        var findResource = await repository.GetResourceAsync<FindPackageByIdResource>();
        var allVersions = await findResource.GetAllVersionsAsync(packageId, cacheContext, logger, CancellationToken.None);

        var versionRange = VersionRange.Parse(packageVersion);
        var targetVersion = versionRange.FindBestMatch(allVersions);

        if (targetVersion == null)
        {
            Log.Logger.Error("未找到符合条件的版本");
            return;
        }
        Log.Logger.Information("找到版本: " + targetVersion.ToString());

        // 找到版本后，添加文件日志
        Directory.CreateDirectory("../log");
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine("../log", $"{packageId.ToLower()}@{targetVersion}.log"), buffered: false)
            .MinimumLevel.Information()
            .CreateLogger();

        #endregion

        #region 下载Nuget包

        var memoryStream = new MemoryStream();

        var success = await findResource.CopyNupkgToStreamAsync(packageId, targetVersion, memoryStream, cacheContext, NullLogger.Instance, CancellationToken.None);
        if (!success)
        {
            Log.Logger.Error("下载失败");
            throw new Exception("下载失败");
        }
        DirectoryInfo nugetDir = new DirectoryInfo(packageId.ToLower());
        var nugetVersionDir = nugetDir.CreateSubdirectory(targetVersion.ToString());

        using var zip = await ZipArchive.CreateAsync(memoryStream, ZipArchiveMode.Read, false, System.Text.Encoding.UTF8);
        Dictionary<FileInfo, ZipArchiveEntry> xmlFiles = new Dictionary<FileInfo, ZipArchiveEntry>();
        foreach (var item in zip.GetFiles())
        {
            if ((item.StartsWith("lib/") || item.StartsWith("ref/")) && item.EndsWith(".xml"))
            {
                Log.Logger.Information("正在提取文件: {File}", item);
                FileInfo info = new FileInfo(Path.Combine(nugetVersionDir.FullName, item));
                info.Directory.Create();
                await zip.GetEntry(item).ExtractToFileAsync(Path.Combine(nugetVersionDir.FullName, item), true);
                xmlFiles[info] = zip.GetEntry(item);
            }
        }

        #endregion

        var chatClient = serviceProvider.GetRequiredService<ChatClient>();
        var cache = serviceProvider.GetRequiredService<IFusionCache>();
        var pipeline = serviceProvider.GetRequiredService<ResiliencePipeline<string>>();
        var sysmsg = ChatMessage.CreateSystemMessage($"""
            你会收到一个c#的xml文档注释。
            你需要翻译为地区/语言代码为："{language}" 所使用的语言，并且保持XML格式不变。
            你的输出必须是可以XElement.Parse(resert)的字符串。
            你被用于API调用进行翻译工作。除了要求你的输出以外不要输出任何对话内容，你的对话内容不会被展示。
            """);
        var chatOptions = new ChatCompletionOptions { TopP = 0.6f };

        Regex regex = new Regex(@"```xml\s*(.*?)\s*```", RegexOptions.Singleline);
        foreach (var item in xmlFiles.Keys)
        {
            var fileread = item.OpenRead();
            var xdoc = await XDocument.LoadAsync(fileread, LoadOptions.PreserveWhitespace, CancellationToken.None);
            await fileread.DisposeAsync();
            XElement membersTranslate = new XElement("members");
            var members = xdoc.Root.Element("members");
            int count = 0;
            int sumCount = members.Elements().Count();
            var memberList = members.Elements("member").ToList();
            long totalInputTokens = 0, totalOutputTokens = 0;
            long totalCalls = 0;
            var concurrency = new SemaphoreSlim(200);
            var tasks = memberList.Select(async member =>
            {
                await concurrency.WaitAsync();
                try
                {
                    var resert = await cache.GetOrSetAsync(member.ToString(), ct => pipeline.ExecuteAsync(async (CancellationToken cance) =>
                      {
                          Log.Logger.Debug("缓存缺失: {Member}", member.Attribute("name")?.Value ?? "default");

                          var completion = await chatClient.CompleteChatAsync([sysmsg, member.ToString()], chatOptions);

                          // 统计 token 用量（无论后续验证是否通过，API 已经消费了）
                          var usage = completion.Value.Usage;
                          if (usage != null)
                          {
                              Interlocked.Add(ref totalInputTokens, usage.InputTokenCount);
                              Interlocked.Add(ref totalOutputTokens, usage.OutputTokenCount);
                              var calls = Interlocked.Increment(ref totalCalls);
                              if (calls % 100 == 0)
                              {
                                  var cost = (totalInputTokens / 1_000_000m * 1m) + (totalOutputTokens / 1_000_000m * 2m);
                                  Log.Logger.Information("已处理 {Calls} 条翻译，累计输入 {In} tokens，输出 {Out} tokens，估算费用 {Cost:F2} 元", calls, totalInputTokens, totalOutputTokens, cost);
                              }
                          }

                          var text = completion.Value.Content[0].Text;
                          if (regex.Match(text) is { Success: true, Groups.Count: > 1 } match)
                          {
                              text = match.Groups[1].Value;
                          }
                          text = text.Trim();

                          XElement e;
                          try
                          {
                              e = XElement.Parse(text);
                          }
                          catch (Exception)
                          {
                              Log.Logger.Debug("无法解析翻译结果为XML，原文: {Original}, 翻译结果: {Translation}", member.ToString(), text);
                              throw;
                          }
                          if ((string?)e.Attribute("name") != (string?)member.Attribute("name"))
                          {
                              Log.Logger.Warning("翻译结果的成员名称与原文不匹配，原文: {Original}, 翻译结果: {Translation}", member.ToString(), text);
                              throw new XmlException("翻译结果的XML格式不正确");
                          }

                          return text;

                      }, ct).AsTask(), tags: [member.Attribute("name")?.Value ?? "default"], token: CancellationToken.None);
                    var e = XElement.Parse(resert);
                    lock (membersTranslate)
                    {
                        membersTranslate.Add(e);
                        Log.Logger.Information("{file} 进度: {Count}/{Total}", xmlFiles[item].FullName, Interlocked.Increment(ref count), sumCount);
                    }
                }
                finally
                {
                    concurrency.Release();
                }
            });
            await Task.WhenAll(tasks);
            // 输出当前文件的 token 消耗汇总
            if (totalCalls > 0)
            {
                var cost = (totalInputTokens / 1_000_000m * 1m) + (totalOutputTokens / 1_000_000m * 2m);
                Log.Logger.Information("文件完成，本文件翻译 {Calls} 条，累计输入 {In} tokens，输出 {Out} tokens，估算费用 {Cost:F2} 元", totalCalls, totalInputTokens, totalOutputTokens, cost);
            }
            members.ReplaceWith(membersTranslate);
            var langDir = item.Directory.CreateSubdirectory(language);
            xdoc.Save(Path.Combine(langDir.FullName, item.Name));
            Log.Logger.Information("完成文件: {File}", Path.Combine(langDir.FullName, item.Name));
        }

        Log.Logger.Information("===== 包 {PackageId} 完成 =====", packageId);
    }
}
