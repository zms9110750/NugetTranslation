using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using System.Xml.Linq;
using ZiggyCreatures.Caching.Fusion;

namespace NugetTranslation.Commands.Tree.Cache.Build;

internal static class CacheBuildCommand
{
    public static readonly Argument<string[]> PackArg = new("pack") { Description = "包名，如 foo" };

    public static Command Create()
    {
        var cmd = new Command("build", "从已翻译的 zh-Hans XML 重建缓存");
        cmd.Add(PackArg);
        cmd.SetAction(Handle);
        return cmd;
    }

    static async Task Handle(ParseResult ctx)
    {
        var code = ctx.GetValue(Root.Code)!;
        var packs = ctx.GetValue(PackArg);

        if (packs is null || packs.Length == 0)
        {
            Console.Error.WriteLine("错误：需要包名参数。用法: cache build <包名>，如 Newtonsoft.Json");
            return;
        }

        int totalWritten = 0;

        foreach (var pkgName in packs)
        {
            var pkgDir = new DirectoryInfo(pkgName.ToLower());
            if (!pkgDir.Exists)
            {
                Console.WriteLine($"  ⚠️ 包目录不存在: {pkgName}");
                continue;
            }

            var langDirName = code;
            var zhFiles = pkgDir.EnumerateFiles("*.xml", SearchOption.AllDirectories)
                .Where(f => f.DirectoryName?.EndsWith(langDirName) == true)
                .ToList();
            if (zhFiles.Count == 0)
            {
                Console.WriteLine($"  - {pkgName}: 无 {langDirName} 文件");
                continue;
            }

            var services = new ServiceCollection();
            var cachePath = Path.GetFullPath(Path.Combine("..", "cache", code, $"{pkgName.ToLower()}.db"));
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            Startup.AddFusionCacheAndSqliteCache(services, cachePath);
            using var sp = services.BuildServiceProvider();
            var cache = sp.GetRequiredService<IFusionCache>();

            int pkgWritten = 0;
            foreach (var zhFile in zhFiles)
            {
                var origPath = zhFile.DirectoryName!.Replace($"\\{langDirName}", "");
                var origFile = new FileInfo(Path.Combine(origPath, zhFile.Name));
                if (!origFile.Exists)
                {
                    continue;
                }

                var origDoc = XDocument.Load(origFile.FullName, LoadOptions.PreserveWhitespace);
                var zhDoc = XDocument.Load(zhFile.FullName, LoadOptions.PreserveWhitespace);

                var origMembers = origDoc.Root?.Element("members")?.Elements("member").ToList();
                var zhLookup = zhDoc.Root?.Element("members")?.Elements("member")
                    .ToDictionary(m => m.Attribute("name")?.Value ?? "", m => m);

                if (origMembers is null || zhLookup is null)
                {
                    continue;
                }

                foreach (var origMember in origMembers)
                {
                    var name = origMember.Attribute("name")?.Value ?? "";
                    if (string.IsNullOrEmpty(name) || !zhLookup.TryGetValue(name, out var zhMember))
                    {
                        continue;
                    }

                    await cache.SetAsync(origMember.ToString(), zhMember.ToString(), tags: [name]);
                    pkgWritten++;
                }
            }

            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={cachePath}");
            conn.Open();
            using var wal = conn.CreateCommand();
            wal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            wal.ExecuteNonQuery();

            totalWritten += pkgWritten;
            Console.WriteLine($"  ✅ {pkgName}: {pkgWritten} 条 -> {cachePath}");
        }

        Console.WriteLine($"\n全部完成，共写入 {totalWritten} 条缓存");
    }
}
