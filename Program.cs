using Microsoft.Extensions.DependencyInjection;
using NuGet.Common;
using NuGet.Packaging;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NugetTranslation;
using OpenAI;
using OpenAI.Chat;
using Polly;
using Serilog;
using System.CommandLine;
using System.IO.Compression;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ZiggyCreatures.Caching.Fusion;

#if DEBUG
Environment.CurrentDirectory = "X:\\MyPackages";
#else 
Directory.CreateDirectory("packages");
Environment.CurrentDirectory = Path.Combine(Environment.CurrentDirectory, "packages");
#endif 

var pack = new Option<string>("--packageId", "package", "-Package", "-p", "-pack", "Include");
var ver = new Option<string>("--packageVersion", "--version", "-v", "-Version", "Version");
var lange = new Option<string>("--Language", "--language", "-l", "-Language", "Language");
var command = new Command(Assembly.GetEntryAssembly()?.GetName().Name!) { pack, ver, lange };
ParseResult parseResult = command.Parse(args);
var packageId = parseResult.GetValue(pack);
if (packageId == null)
{
    throw new ArgumentNullException(nameof(packageId), "请提供包ID");
}
var packageVersion = parseResult.GetValue(ver) ?? "*";
var language = parseResult.GetValue(lange) ?? "zh-Hans";

var build = new ServiceCollection();
build.AddChatClient();
build.AddFusionCacheAndSqliteCache(Path.Combine("..","cache",packageId.ToLower(), language + ".sqlite.db"));
build.AddSourceRepository();
build.AddSingleton<SourceCacheContext>();
build.AddSingleton(NullLogger.Instance);
build.AddPolly();

var serviceProvider = build.BuildServiceProvider();

Log.Logger = new LoggerConfiguration()
           .Enrich.FromLogContext()
           .WriteTo.Console()
           .MinimumLevel.Debug()
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
    你被用于API调用进行翻译工作。除了要求你的输出以外不要输出任何对话内容，这里没有上下文。
    你会收到一个c#的xml文档注释。
    地区/语言代码为："{language}"，你需要将内容翻译为此代码使用的语言，并且保持XML格式不变。
    你的输出必须是可以XElement.Parse(resert)的字符串。
    """);
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
    await Parallel.ForEachAsync(members.Elements("member"), async (member, token) =>
    {
        var resert = await cache.GetOrSetAsync(member.ToString(), ct => pipeline.ExecuteAsync(async (CancellationToken cance) =>
          {
              Log.Logger.Verbose("缓存缺失: {Member}", member.Attribute("name")?.Value ?? "default");
              var completion = await chatClient.CompleteChatAsync([sysmsg, member.ToString()])
              .WaitAsync(TimeSpan.FromSeconds(60), cance);

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
          }, ct).AsTask(), tags: [member.Attribute("name")?.Value ?? "default"], token: token);
        var e = XElement.Parse(resert);
        lock (membersTranslate)
        {
            membersTranslate.Add(e);
            Log.Logger.Information("{file} 进度: {Count}/{Total}", xmlFiles[item].FullName, Interlocked.Increment(ref count), sumCount);
        }
    });
    members.ReplaceWith(membersTranslate);
    var langDir = item.Directory.CreateSubdirectory(language);
    xdoc.Save(Path.Combine(langDir.FullName, item.Name));
    Log.Logger.Information("完成文件: {File}", Path.Combine(langDir.FullName, item.Name));
}
