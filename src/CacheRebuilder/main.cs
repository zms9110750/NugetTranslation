#!/usr/bin/env -S dotnet --

#:package Microsoft.Data.Sqlite@*
#:package System.Text.Json@*

using System.Text.Json;
using System.Xml.Linq;

// 每个缓存条目的过期时间：1000 年后
var farFuture = new DateTimeOffset(3026, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

var packagesDir = new DirectoryInfo("packages");
var cacheDir = new DirectoryInfo("cache");

if (!packagesDir.Exists)
{
    Console.Error.WriteLine("packages/ 目录不存在");
    return;
}

var packageDirs = packagesDir.EnumerateDirectories()
    .Where(d => d.Name != "log" && d.Name != "cache")
    .ToList();

Console.WriteLine($"找到 {packageDirs.Count} 个包目录");

int totalEntries = 0;

foreach (var pkgDir in packageDirs)
{
    var pkgName = pkgDir.Name;
    
    // 找所有 zh-Hans 翻译文件
    var zhFiles = pkgDir.EnumerateFiles("*.xml", SearchOption.AllDirectories)
        .Where(f => f.DirectoryName?.EndsWith("zh-Hans") == true)
        .ToList();

    if (zhFiles.Count == 0) continue;

    // 语言目录固定为 zh-Hans
    var langDir = "zh-Hans";

    // 构建缓存 DB 路径
    var cacheDbPath = Path.GetFullPath(Path.Combine(cacheDir.FullName, langDir, $"{pkgName}.db"));
    Directory.CreateDirectory(Path.GetDirectoryName(cacheDbPath)!);

    // 创建 SQLite 缓存库
    var connString = $"Data Source={cacheDbPath}";
    using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connString);
    conn.Open();

    // 建表（如果不存在）
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS "cache" (
            "key"	varchar NOT NULL,
            "value"	BLOB,
            "expiry"	INTEGER,
            "renewal"	INTEGER,
            PRIMARY KEY("key")
        ) WITHOUT ROWID;
        CREATE TABLE IF NOT EXISTS "meta" (
            "key"	TEXT NOT NULL,
            "value"	INTEGER,
            PRIMARY KEY("key")
        ) WITHOUT ROWID;
        """;
    cmd.ExecuteNonQuery();

    int pkgEntries = 0;

    foreach (var zhFile in zhFiles)
    {
        // 找对应的原始 XML（去掉路径中的 zh-Hans）
        var origPath = zhFile.DirectoryName!.Replace($"\\zh-Hans", "");
        var origFileName = zhFile.Name;
        var origFile = new FileInfo(Path.Combine(origPath, origFileName));
        
        if (!origFile.Exists)
        {
            Console.WriteLine($"  ⚠️ {pkgName}: 找不到原文 {origFile.FullName}");
            continue;
        }

        // 读取原文 XML（必须 PreserveWhitespace，否则 key 与主程序不匹配）
        var origDoc = XDocument.Load(origFile.FullName, LoadOptions.PreserveWhitespace);
        var zhDoc = XDocument.Load(zhFile.FullName);

        var origMembers = origDoc.Root?.Element("members")?.Elements("member").ToDictionary(
            m => m.Attribute("name")?.Value ?? "",
            m => m.ToString());

        var zhMembers = zhDoc.Root?.Element("members")?.Elements("member").ToDictionary(
            m => m.Attribute("name")?.Value ?? "",
            m => m.ToString());

        if (origMembers == null || zhMembers == null) continue;

        // 按 name 匹配并写入缓存
        using var insertCmd = conn.CreateCommand();
        insertCmd.CommandText = "INSERT OR REPLACE INTO cache (key, value, expiry, renewal) VALUES (@key, @value, @expiry, @renewal)";
        var keyParam = insertCmd.Parameters.Add("@key", Microsoft.Data.Sqlite.SqliteType.Text);
        var valParam = insertCmd.Parameters.Add("@value", Microsoft.Data.Sqlite.SqliteType.Blob);
        var expParam = insertCmd.Parameters.Add("@expiry", Microsoft.Data.Sqlite.SqliteType.Integer);
        var renParam = insertCmd.Parameters.Add("@renewal", Microsoft.Data.Sqlite.SqliteType.Integer);
        expParam.Value = farFuture;
        renParam.Value = 0;

        int matched = 0;
        foreach (var (name, origXml) in origMembers)
        {
            if (zhMembers.TryGetValue(name, out var zhXml))
            {
                keyParam.Value = $"v2:{origXml}";
                valParam.Value = System.Text.Encoding.UTF8.GetBytes(zhXml);
                insertCmd.ExecuteNonQuery();
                matched++;
            }
        }

        pkgEntries += matched;
        Console.WriteLine($"  {pkgName}/{origFileName}: 匹配 {matched}/{origMembers.Count} 条");
    }

    // WAL checkpoint
    using var walCmd = conn.CreateCommand();
    walCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
    walCmd.ExecuteNonQuery();

    totalEntries += pkgEntries;
    Console.WriteLine($"  ✅ {pkgName}: 共写入 {pkgEntries} 条缓存 -> {cacheDbPath}");
}

Console.WriteLine($"\n全部完成，共写入 {totalEntries} 条缓存条目");
