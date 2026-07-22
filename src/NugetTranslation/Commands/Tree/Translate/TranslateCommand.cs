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
    public static readonly Argument<string[]> PackArg = new("pack") { Description = "包名@版本，如 foo@1.0 或 foo（版本默认为 *）" };

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

        // —— 配置 ——
        var config = ConfigLoader.Instance ?? throw new InvalidOperationException("配置未加载");
        var profile = flags is not null ? config[flags] : null;
        var ai = profile?.Ai ?? throw new InvalidOperationException("AI 配置未找到");
        ai.EnsureResolved();

        var chatClient = new ChatClient(
            ai.Model.Resolve(),
            new ApiKeyCredential(ai.Key.Resolve()),
            new OpenAIClientOptions { Endpoint = new Uri(ai.Url.Resolve()) });

        // —— 解析版本 ——
        var repo = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var cacheContext = new SourceCacheContext();
        var findResource = await repo.GetResourceAsync<FindPackageByIdResource>();

        var packages = await ParseSpecsAsync(rawSpecs, findResource, cacheContext);

        // —— 日志 ——
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .MinimumLevel.Information()
            .CreateLogger();

        // —— 注册命名缓存 + 全局 DI ——
        var services = new ServiceCollection();
        services.AddSingleton(new Translator(chatClient, code));
        services.AddSingleton(NullLogger.Instance);

        var cacheDir = Path.GetFullPath(Path.Combine("..", "cache", code));
        Directory.CreateDirectory(cacheDir);
        foreach (var id in packages.Keys)
        {
            services.AddFusionCacheAndSqliteCache(id.ToLower(), Path.Combine(cacheDir, id.ToLower() + ".db"));
        }

        services.AddTransient<PackageProcessor>();

        using var sp = services.BuildServiceProvider();

        // —— 逐版本翻译 ——
        foreach (var (id, versions) in packages)
        {
            foreach (var ver in versions)
            {
                Console.WriteLine($"\n===== 翻译: {id}@{ver} =====");

                try
                {
                    Log.Logger = new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .WriteTo.Console()
                        .WriteTo.File(Path.Combine("../log", $"{id.ToLower()}@{ver}.log"), buffered: false)
                        .MinimumLevel.Information()
                        .CreateLogger();

                    var zip = await DownloadPackageAsync(id, ver, findResource, cacheContext);
                    var processor = sp.GetRequiredService<PackageProcessor>();
                    await processor.ProcessAsync(id, ver, zip, code);

                    Console.WriteLine($"  ?? {id}@{ver} 完成");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ?? {id}@{ver}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
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

            if (!allVersions.TryGetValue(id, out IEnumerable<NuGetVersion>? available))
            {
                available = await findResource.GetAllVersionsAsync(
                    id, cacheContext, NullLogger.Instance, CancellationToken.None);
                allVersions[id] = available;
            }

            var range = VersionRange.Parse(verSpec);
            IEnumerable<NuGetVersion> matched;

            if (range.MaxVersion is null)
            {
                var best = range.FindBestMatch(available);
                matched = best is not null ? [best] : [];
            }
            else
            {
                var includePrerelease = range.MinVersion?.IsPrerelease == true
                                     || range.MaxVersion?.IsPrerelease == true;
                matched = available.Where(v => range.Satisfies(v)
                                            && (includePrerelease || !v.IsPrerelease));
            }

            if (result.TryGetValue(id, out var set))
            {
                set.UnionWith(matched);
            }
            else
            {
                result[id] = new HashSet<NuGetVersion>(matched);
            }
        }

        return result;
    }

    /// <summary>下载指定包的 .nupkg 文件，返回 ZipArchive。同时同步解压到 packages/{包名}/{版本}/ 作为本地副本。</summary>
    static async Task<ZipArchive> DownloadPackageAsync(string packageId, NuGetVersion version,
        FindPackageByIdResource findResource, SourceCacheContext cacheContext)
    {
        var ms = new MemoryStream();
        var ok = await findResource.CopyNupkgToStreamAsync(packageId, version, ms, cacheContext, NullLogger.Instance, CancellationToken.None);
        if (!ok)
        {
            throw new InvalidOperationException($"下载失败: {packageId}@{version}");
        }

        var zip = await ZipArchive.CreateAsync(ms, ZipArchiveMode.Read, false, System.Text.Encoding.UTF8);

        // 同步解压到磁盘作为本地副本（已有且 sha256 一致则跳过）
        var baseDir = Path.Combine(packageId.ToLower(), version.ToString());
        using var sha256 = SHA256.Create();

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
                {
                    continue;
                }
            }

            fi.Directory.Create();
            entry.ExtractToFile(fi.FullName, overwrite: true);
        }
        return zip;

        byte[] HashStream(Stream s)
        {
            var pos = s.Position;
            var h = sha256.ComputeHash(s);
            s.Position = pos;
            return h;
        }
    }
}
