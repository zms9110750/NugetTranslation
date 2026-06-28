// CacheRebuilder — 从 zh-Hans XML 重建缓存（走 IFusionCache）
using Microsoft.Extensions.DependencyInjection;
using System.Xml.Linq;
using NugetTranslation;
using ZiggyCreatures.Caching.Fusion;

// 工作目录
var probeDir = new DirectoryInfo(AppContext.BaseDirectory);
while (probeDir != null && !probeDir.GetFiles("NugetTranslation.slnx").Any())
    probeDir = probeDir.Parent;
if (probeDir != null)
    Environment.CurrentDirectory = probeDir.FullName;
Directory.CreateDirectory("packages");
Environment.CurrentDirectory = Path.Combine(Environment.CurrentDirectory, "packages");

var langDir = "zh-Hans";
var packagesDir = new DirectoryInfo(".");

var pkgDirs = packagesDir.EnumerateDirectories()
    .Where(d => d.Name != "log" && d.Name != "cache")
    .ToList();

Console.WriteLine($"找到 {pkgDirs.Count} 个包目录");

int totalWritten = 0;

foreach (var pkgDir in pkgDirs)
{
    var pkgName = pkgDir.Name;

    // 找 zh-Hans 文件
    var zhFiles = pkgDir.EnumerateFiles("*.xml", SearchOption.AllDirectories)
        .Where(f => f.DirectoryName?.EndsWith(langDir) == true)
        .ToList();
    if (zhFiles.Count == 0) continue;

    // 构建 FusionCache（每个包独立，SQLite DB 按包名分）
    var services = new ServiceCollection();
    var cachePath = Path.GetFullPath(Path.Combine("..", "cache", langDir, $"{pkgName}.db"));
    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
    services.AddFusionCacheAndSqliteCache(cachePath);
    using var sp = services.BuildServiceProvider();
    var cache = sp.GetRequiredService<IFusionCache>();

    int pkgWritten = 0;

    foreach (var zhFile in zhFiles)
    {
        // 对应原文路径
        var origPath = zhFile.DirectoryName!.Replace($"\\{langDir}", "");
        var origFileName = zhFile.Name;
        var origFile = new FileInfo(Path.Combine(origPath, origFileName));
        if (!origFile.Exists) continue;

        // 读取原文和翻译
        var origDoc = XDocument.Load(origFile.FullName, LoadOptions.PreserveWhitespace);
        var zhDoc = XDocument.Load(zhFile.FullName, LoadOptions.PreserveWhitespace);

        var origMembers = origDoc.Root?.Element("members")?.Elements("member").ToList();
        var zhLookup = zhDoc.Root?.Element("members")?.Elements("member")
            .ToDictionary(m => m.Attribute("name")?.Value ?? "", m => m);

        if (origMembers == null || zhLookup == null) continue;

        foreach (var origMember in origMembers)
        {
            var name = origMember.Attribute("name")?.Value ?? "";
            if (string.IsNullOrEmpty(name) || !zhLookup.TryGetValue(name, out var zhMember)) continue;

            var key = origMember.ToString();
            var value = zhMember.ToString();

            await cache.SetAsync(key, value);
            pkgWritten++;
        }
    }

    // 强制 WAL checkpoint
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={cachePath}");
    conn.Open();
    using var wal = conn.CreateCommand();
    wal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
    wal.ExecuteNonQuery();

    totalWritten += pkgWritten;
    Console.WriteLine($"  ✅ {pkgName}: {pkgWritten} 条 -> {cachePath}");
}

Console.WriteLine($"\n全部完成，共写入 {totalWritten} 条缓存");
