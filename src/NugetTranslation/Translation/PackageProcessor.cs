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
    private long _totalInputTokens, _totalOutputTokens, _cacheHitTokens;
    private int _done, _total;

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
            "{Pkg}@{Ver} 缺失 {Count} 条数据，预测为 {Tokens:F3}M token",
            packageId, targetVersion, missingSet.Count, estimatedTokens / 1_000_000.0);

        // 4. 并发翻译
        if (missingSet.Count > 0)
        {
            _total = missingSet.Count;
            var semaphore = new SemaphoreSlim(500, 500);
            var tasks = missingSet.Select(key => TranslateOneAsync(key, cache, semaphore));
            await Task.WhenAll(tasks);

            Log.Logger.Information(
                "{Pkg} 翻译完成，输入 {In:F3}M tok，输出 {Out:F3}M tok，命中 {Cache:F3}M tok",
                packageId,
                _totalInputTokens / 1_000_000.0,
                _totalOutputTokens / 1_000_000.0,
                _cacheHitTokens / 1_000_000.0);
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
                if (!maybe.HasValue || maybe.Value is null)
                    throw new InvalidOperationException($"缓存缺失: {member.Attribute("name")?.Value}");
                translated.Add(XElement.Parse(maybe.Value, LoadOptions.PreserveWhitespace));
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

    /// <summary>翻译一条 member，并发安全。</summary>
    private async Task TranslateOneAsync(string key, IFusionCache cache, SemaphoreSlim semaphore)
    {
        await semaphore.WaitAsync();
        try
        {
            var nameAttr = XElement.Parse(key).Attribute("name")?.Value ?? "";
            bool wasCached = true;

            try
            {
                await cache.GetOrSetAsync(key,
                    async ct =>
                    {
                        wasCached = false;
                        var result = await _translator.TranslateMemberAsync(key, readme: null, ct);
                        var usage = _translator.LastUsage;
                        if (usage is not null)
                        {
                            Interlocked.Add(ref _totalInputTokens, usage.InputTokenCount ?? 0);
                            Interlocked.Add(ref _totalOutputTokens, usage.OutputTokenCount ?? 0);
                        }
                        return result.ToString();
                    },
                    MaybeValue<string>.None,
                    (FusionCacheEntryOptions?)null,
                    tags: [nameAttr],
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Logger.Error("翻译失败 [{Name}]: {Error}", nameAttr, ex.Message);
            }

            var processed = Interlocked.Increment(ref _done);

            if (wasCached)
            {
                var savedTokens = _tokenizer.CountTokens(key,
                    considerPreTokenization: true, considerNormalization: true);
                Interlocked.Add(ref _cacheHitTokens, savedTokens);
            }

            if (processed % 100 == 0)
            {
                Log.Logger.Information(
                    "当前完成 {Done}/{Total}，消耗token:命中 {Cache:F3}M，输入 {In:F3}M：输出 {Out:F3}M",
                    processed, _total,
                    _cacheHitTokens / 1_000_000.0,
                    _totalInputTokens / 1_000_000.0,
                    _totalOutputTokens / 1_000_000.0);
            }
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
