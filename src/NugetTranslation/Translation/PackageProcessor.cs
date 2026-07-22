using Microsoft.ML.Tokenizers;
using NuGet.Versioning;
using Serilog;
using System.IO.Compression;
using System.Xml.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace NugetTranslation.Translation;

/// <summary>包翻译处理器。DI 瞬态，每版本一个独立实例。</summary>
internal sealed class PackageProcessor
{
    private readonly IFusionCacheProvider _cacheProvider;
    private readonly Translator _translator;
    private static readonly TiktokenTokenizer _tokenizer = TiktokenTokenizer.CreateForEncoding("cl100k_base");
    private long _totalInputTokens, _totalOutputTokens;

    public PackageProcessor(IFusionCacheProvider cacheProvider, Translator translator)
    {
        _cacheProvider = cacheProvider;
        _translator = translator;
    }

    /// <summary>处理一个包的一个版本。</summary>
    public async Task ProcessAsync(string packageId, NuGetVersion targetVersion, ZipArchive zip, string language)
    {
        var cache = _cacheProvider.GetCache(packageId.ToLower());

        // 1. 筛选 XML 为原件文档集
        var documents = LoadXmlDocuments(zip);
        if (documents.Count == 0)
        {
            Log.Logger.Warning("包 {Id} 无可翻译的 XML 文件", packageId);
            return;
        }

        // 2. 预检：查找缺失的 member
        var missingSet = new HashSet<string>();
        foreach (var doc in documents.Values)
        {
            var members = doc.Root?.Element("members")?.Elements("member");
            if (members is null) continue;

            foreach (var member in members)
            {
                var key = member.ToString();
                var maybe = await cache.TryGetAsync<string>(key);
                if (!maybe.HasValue)
                    missingSet.Add(key);
            }
        }

        // 3. Token 预估
        long estimatedTokens = 0;
        foreach (var key in missingSet)
            estimatedTokens += _tokenizer.CountTokens(key, considerPreTokenization: true, considerNormalization: true);

        Log.Logger.Information(
            "{Pkg} 缺失 {Count} 条数据，预测为 {Tokens:F3}M token",
            packageId, missingSet.Count, estimatedTokens / 1_000_000.0);

        // 4. 并发翻译
        if (missingSet.Count > 0)
        {
            var semaphore = new SemaphoreSlim(500, 500);
            var tasks = missingSet.Select(key => TranslateOneAsync(key, cache, semaphore));
            await Task.WhenAll(tasks);

            Log.Logger.Information(
                "{Pkg} 翻译完成，输入 {In} tok，输出 {Out} tok",
                packageId, _totalInputTokens, _totalOutputTokens);
        }

        // 5. 替换输出
        foreach (var (entryPath, doc) in documents)
        {
            var members = doc.Root?.Element("members")?.Elements("member").ToList();
            if (members is null || members.Count == 0) continue;

            var translated = new XElement("members");
            foreach (var member in members)
            {
                var maybe = await cache.TryGetAsync<string>(member.ToString());
                if (maybe.HasValue && maybe.Value is { } cached)
                    translated.Add(XElement.Parse(cached));
            }

            members[0].Parent?.ReplaceWith(translated);

            var outputDir = Path.GetFullPath(Path.Combine(
                packageId.ToLower(), targetVersion.ToString(),
                Path.GetDirectoryName(entryPath) ?? "", language));
            Directory.CreateDirectory(outputDir);
            doc.Save(Path.Combine(outputDir, Path.GetFileName(entryPath)), SaveOptions.DisableFormatting);
            Log.Logger.Information("已写出: {Path}", Path.Combine(outputDir, Path.GetFileName(entryPath)));
        }
    }

    private int _done;

    /// <summary>翻译一条 member，并发安全。</summary>
    private async Task TranslateOneAsync(string key, IFusionCache cache, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            await cache.GetOrSetAsync(key, async ct =>
            {
                var result = await _translator.TranslateMemberAsync(key, readme: null, ct);
                var usage = _translator.LastUsage;
                if (usage is not null)
                {
                    Interlocked.Add(ref _totalInputTokens, usage.InputTokenCount ?? 0);
                    Interlocked.Add(ref _totalOutputTokens, usage.OutputTokenCount ?? 0);
                }
                return result.ToString();
            });

            var done = Interlocked.Increment(ref _done);
            if (done % 100 == 0)
                Log.Logger.Information("翻译进度: {Done}", done);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>从 zip 筛选 lib/ref 下的 .xml 文件，加载为 XDocument。</summary>
    private static Dictionary<string, XDocument> LoadXmlDocuments(ZipArchive zip)
    {
        var docs = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName;
            if (!(path.StartsWith("lib/") || path.StartsWith("ref/")) || !path.EndsWith(".xml"))
                continue;

            using var stream = entry.Open();
            docs[path] = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        return docs;
    }
}
