#!/usr/bin/env -S dotnet --

#:package Microsoft.Data.Sqlite@*

using System.Xml.Linq;
using Microsoft.Data.Sqlite;

var root = ".";
var packagesDir = Path.Combine(root, "packages");
var cacheDir = Path.Combine(root, "cache", "zh-Hans");

var pkgDirs = Directory.EnumerateDirectories(packagesDir)
    .Where(d => d != Path.Combine(packagesDir, "log"))
    .ToList();

Console.WriteLine($"检查 {pkgDirs.Count} 个包...");

int totalMembers = 0, totalHits = 0, totalMisses = 0;
var missDetails = new List<(string Pkg, string Name)>();

foreach (var pkgDir in pkgDirs)
{
    var pkgName = Path.GetFileName(pkgDir);

    // 不检查没有 zh-Hans 的包
    if (!Directory.EnumerateDirectories(pkgDir, "zh-Hans", SearchOption.AllDirectories).Any())
        continue;

    // 找原始 XML
    var origFiles = Directory.EnumerateFiles(pkgDir, "*.xml", SearchOption.AllDirectories)
        .Where(f => !f.Contains("zh-Hans"))
        .ToList();

    if (origFiles.Count == 0) continue;

    // 打开缓存 DB
    var dbPath = Path.Combine(cacheDir, $"{pkgName}.db");
    if (!File.Exists(dbPath))
    {
        Console.WriteLine($"  ❌ {pkgName}: 缓存 DB 不存在");
        continue;
    }

    using var conn = new SqliteConnection($"Data Source={dbPath}");
    conn.Open();

    int pkgHits = 0, pkgMisses = 0;
    var checkedNames = new HashSet<string>();

    foreach (var file in origFiles)
    {
        var doc = XDocument.Load(file, LoadOptions.PreserveWhitespace);
        var members = doc.Root?.Element("members")?.Elements("member") ?? [];

        foreach (var member in members)
        {
            var name = member.Attribute("name")?.Value ?? "";
            if (string.IsNullOrEmpty(name) || !checkedNames.Add(name)) continue;

            var key = $"v2:{member}";
            totalMembers++;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM cache WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", key);
            var count = (long)cmd.ExecuteScalar();

            if (count > 0)
                pkgHits++;
            else
            {
                pkgMisses++;
                missDetails.Add((pkgName, name));
            }
        }
    }

    totalHits += pkgHits;
    totalMisses += pkgMisses;

    if (pkgMisses > 0)
        Console.WriteLine($"  ⚠️ {pkgName}: {pkgHits}/{pkgHits + pkgMisses} 命中 ({pkgMisses} 缺失)");
    else if (pkgHits > 0)
        Console.WriteLine($"  ✅ {pkgName}: {pkgHits} 全部命中");
}

Console.WriteLine($"\n总计: {totalMembers} 个成员, {totalHits} 命中, {totalMisses} 缺失 ({totalHits * 100.0 / totalMembers:F1}%)");

if (missDetails.Count > 0)
{
    Console.WriteLine("\n缺失列表 (前 20):");
    foreach (var (pkg, name) in missDetails.Take(20))
        Console.WriteLine($"  {pkg}: {name}");
}
