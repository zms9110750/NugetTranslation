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
        var flags = ctx.GetValue(Root.Profile);

        if (rawSpecs is null || rawSpecs.Length == 0)
        {
            Console.Error.WriteLine("错误：需要包名参数。用法: translate <包名>@<版本>，如 Newtonsoft.Json@13.0.3");
            return;
        }

        var config = ConfigLoader.Instance ?? throw new InvalidOperationException("配置未加载");
        var profile = flags is not null ? config[flags] : null;
        var ai = profile?.Ai ?? throw new InvalidOperationException($"错误：未找到 profile \"{flags}\" 的 AI 配置。请检查 appsettings.json。");
        ai.EnsureResolved();

        var chatClient = new ChatClient(
            ai.Model.Resolve(),
            new ApiKeyCredential(ai.Key.Resolve()),
            new OpenAIClientOptions { Endpoint = new Uri(ai.Url.Resolve()) });

        var repo = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var cacheContext = new SourceCacheContext();
        var findResource = await repo.GetResourceAsync<FindPackageByIdResource>();

        var packages = await ParseSpecsAsync(rawSpecs, findResource, cacheContext);

        var logLevel = profile?.LogLevel ?? Serilog.Events.LogEventLevel.Information;
        Log.Logger = new LoggerConfiguration()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .MinimumLevel.Is(logLevel)
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton(new Translator(chatClient, code));
        services.AddSingleton(NullLogger.Instance);
        services.AddTransient<PackageProcessor>();

        var cacheDir = Path.GetFullPath(Path.Combine("..", "cache", code));
        Directory.CreateDirectory(cacheDir);
        foreach (var id in packages.Keys)
            services.AddFusionCacheAndSqliteCache(id.ToLower(), Path.Combine(cacheDir, id.ToLower() + ".db"));

        using var sp = services.BuildServiceProvider();

        // 按版本顺序执行：预检 → 翻译 → 产出 XML，一个版本完成再下一个
        foreach (var (id, versions) in packages)
        {
            foreach (var ver in versions)
            {
                Console.WriteLine($"===== 翻译: {id}@{ver} =====");

                try
                {
                    Log.Logger = new LoggerConfiguration()
                        .Enrich.FromLogContext()
                        .WriteTo.Console()
                        .WriteTo.File(Path.Combine("../log", $"{id.ToLower()}@{ver}.log"), buffered: false)
                        .MinimumLevel.Is(logLevel)
                        .CreateLogger();

                    var zip = await DownloadPackageAsync(id, ver, findResource, cacheContext);
                    var processor = sp.GetRequiredService<PackageProcessor>();
                    await processor.ProcessAsync(id, ver, zip, code);

                    Console.WriteLine($"  OK {id}@{ver}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  NG {id}@{ver}: {ex.GetType().Name}: {ex.Message}");
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
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return sha256.ComputeHash(ms.ToArray());
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
