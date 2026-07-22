using Microsoft.Extensions.DependencyInjection;
using NuGet.Common;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NugetTranslation.Configuration;
using NugetTranslation.Translation;
using OpenAI;
using OpenAI.Chat;
using Serilog;
using System.ClientModel;
using System.CommandLine;
using System.IO.Compression;
using System.Security.Cryptography;
using ZiggyCreatures.Caching.Fusion;

namespace NugetTranslation.Commands.Tree.Translate;

internal static class TranslateCommand
{
    public static readonly Argument<string[]> PackArg = new("pack") { Description = "包名@版本" };

    public static Command Create()
    {
        var cmd = new Command("translate", "翻译 NuGet XML 文档注释");
        cmd.Add(PackArg);
        cmd.SetAction(Handle);
        return cmd;
    }

    static async Task Handle(ParseResult ctx)
    {
        var code = ctx.GetValue(Root.Code)!;
        var rawSpecs = ctx.GetValue(PackArg);
        var flags = ctx.GetValue(Root.Flags);

        if (rawSpecs is null || rawSpecs.Length == 0)
        {
            Console.Error.WriteLine("需要 pack");
            return;
        }

        var config = ConfigLoader.Instance ?? throw new InvalidOperationException("配置未加载");
        var profile = flags is not null ? config[flags] : null;
        var ai = profile?.Ai ?? throw new InvalidOperationException("AI 配置未找到");
        ai.EnsureResolved();

        var chatClient = new ChatClient(
            ai.Model.Resolve(),
            new ApiKeyCredential(ai.Key.Resolve()),
            new OpenAIClientOptions { Endpoint = new Uri(ai.Url.Resolve()) });

        var repo = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var cacheContext = new SourceCacheContext();
        var findResource = await repo.GetResourceAsync<FindPackageByIdResource>();

        var packages = await ParseSpecsAsync(rawSpecs, findResource, cacheContext);

        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .MinimumLevel.Information()
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton(new Translator(chatClient, code));
        services.AddSingleton(NullLogger.Instance);

        var cacheDir = Path.GetFullPath(Path.Combine("..", "cache", code));
        Directory.CreateDirectory(cacheDir);
        foreach (var id in packages.Keys)
            services.AddFusionCacheAndSqliteCache(id.ToLower(), Path.Combine(cacheDir, id.ToLower() + ".db"));

        using var sp = services.BuildServiceProvider();

        // 收集所有 (包, 版本) 对，并发下载
        var allPairs = packages
            .SelectMany(kv => kv.Value.Select(v => (PackageId: kv.Key, Version: v)))
            .ToList();

        var downloadSemaphore = new SemaphoreSlim(50, 50);

        async Task ProcessOneAsync(string packageId, NuGetVersion version)
        {
            Console.WriteLine($"===== 翻译: {packageId}@{version} =====");

            try
            {
                Log.Logger = new LoggerConfiguration()
                    .Enrich.FromLogContext()
                    .WriteTo.Console()
                    .WriteTo.File(Path.Combine("../log", $"{packageId.ToLower()}@{version}.log"), buffered: false)
                    .MinimumLevel.Information()
                    .CreateLogger();

                await downloadSemaphore.WaitAsync();
                ZipArchive zip;
                try { zip = await DownloadPackageAsync(packageId, version, findResource, cacheContext); }
                finally { downloadSemaphore.Release(); }

                var processor = sp.GetRequiredService<PackageProcessor>();
                await processor.ProcessAsync(packageId, version, zip, code);

                Console.WriteLine($"  OK {packageId}@{version}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  NG {packageId}@{version}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var tasks = allPairs.Select(p => ProcessOneAsync(p.PackageId, p.Version));
        await Task.WhenAll(tasks);
    }

    static async Task<Dictionary<string, HashSet<NuGetVersion>>> ParseSpecsAsync(string[] rawSpecs, FindPackageByIdResource findResource, SourceCacheContext cacheContext)
    {
        var allVersions = new Dictionary<string, IEnumerable<NuGetVersion>>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, HashSet<NuGetVersion>>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in rawSpecs)
        {
            var at = s.IndexOf('@');
            var id = at < 0 ? s : s[..at];
            var verSpec = at < 0 ? "*" : s[(at + 1)..];

            // 格式校验
            if (at >= 0 && string.IsNullOrWhiteSpace(verSpec))
            {
                Console.Error.WriteLine($"格式错误: \"{s}\"，应为 packname 或 packname@版本规范");
                continue;
            }
            if (string.IsNullOrWhiteSpace(id))
            {
                Console.Error.WriteLine($"格式错误: \"{s}\"，包名不能为空");
                continue;
            }

            if (!allVersions.TryGetValue(id, out IEnumerable<NuGetVersion>? available))
            {
                available = await findResource.GetAllVersionsAsync(
                    id, cacheContext, NullLogger.Instance, CancellationToken.None);
                allVersions[id] = available;
            }

            var range = VersionRange.Parse(verSpec);
            IEnumerable<NuGetVersion> matched;

            // 用 NuGet API 判断：IsFloating 为浮点版本（*、1.3.* 等），取最佳匹配一个
            // 以 [ 或 ( 开头为手写范围版本，匹配全部
            // 其余为裸版本号（1.0 等），取最佳匹配一个
            if (range.IsFloating)
            {
                var best = range.FindBestMatch(available);
                matched = best is not null ? [best] : [];
            }
            else if (verSpec.StartsWith('[') || verSpec.StartsWith('('))
            {
                var includePrerelease = range.MinVersion?.IsPrerelease == true
                                     || range.MaxVersion?.IsPrerelease == true;
                matched = available.Where(v => range.Satisfies(v)
                                            && (includePrerelease || !v.IsPrerelease));
            }
            else
            {
                var best = range.FindBestMatch(available);
                matched = best is not null ? [best] : [];
            }

            if (result.TryGetValue(id, out var set))
                set.UnionWith(matched);
            else
                result[id] = new HashSet<NuGetVersion>(matched);
        }

        return result;
    }

    static async Task<ZipArchive> DownloadPackageAsync(string packageId, NuGetVersion version,
        FindPackageByIdResource findResource, SourceCacheContext cacheContext)
    {
        var ms = new MemoryStream();
        var ok = await findResource.CopyNupkgToStreamAsync(packageId, version, ms, cacheContext, NullLogger.Instance, CancellationToken.None);
        if (!ok)
            throw new InvalidOperationException($"下载失败: {packageId}@{version}");

        var zip = await ZipArchive.CreateAsync(ms, ZipArchiveMode.Read, false, System.Text.Encoding.UTF8);

        var baseDir = Path.Combine(packageId.ToLower(), version.ToString());
        using var sha256 = SHA256.Create();
        byte[] HashStream(Stream s)
        {
            var pos = s.Position;
            var h = sha256.ComputeHash(s);
            s.Position = pos;
            return h;
        }

        foreach (var entry in zip.Entries)
        {
            var path = entry.FullName;
            if (!(path.StartsWith("lib/") || path.StartsWith("ref/")) || !path.EndsWith(".xml"))
                continue;

            var fi = new FileInfo(Path.Combine(baseDir, path));

            if (fi.Exists)
            {
                using var existing = fi.OpenRead();
                using var entryStream = entry.Open();
                if (HashStream(existing).AsSpan().SequenceEqual(HashStream(entryStream)))
                    continue;
            }

            fi.Directory.Create();
            entry.ExtractToFile(fi.FullName, overwrite: true);
        }

        return zip;
    }
}
