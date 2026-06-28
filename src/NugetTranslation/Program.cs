using Microsoft.Data.Sqlite;
using System.CommandLine;
using System.Reflection;
using NugetTranslation;

#if LOCAL
var probeDir = new DirectoryInfo(AppContext.BaseDirectory);
while (probeDir != null && !probeDir.GetFiles("NugetTranslation.slnx").Any())
    probeDir = probeDir.Parent;
if (probeDir != null)
    Environment.CurrentDirectory = probeDir.FullName;
#endif
Directory.CreateDirectory("packages");
Environment.CurrentDirectory = Path.Combine(Environment.CurrentDirectory, "packages");

// ===== 选项 =====
var codeOpt     = new Option<string>("-code");
var packOpt     = new Option<string>("-pack");
var removeOpt   = new Option<string>("-remove");
var packOld     = new Option<string>("--packageId", "package", "-Package", "-p", "-pack", "Include");
var verOld      = new Option<string>("--packageVersion", "--version", "-v", "-Version", "Version");
var langOld     = new Option<string>("--Language", "--language", "-l", "-Language", "Language");

// ===== 命令 =====
var translateCmd = new Command("translate", "翻译包  -code zh-Hans -pack name@ver,name@ver");
translateCmd.Add(codeOpt);
translateCmd.Add(packOpt);
translateCmd.SetAction(HandleTranslate);

var removeCmd = new Command("remove", "删除缓存  -code zh-Hans -remove pkg@memberName");
removeCmd.Add(codeOpt);
removeCmd.Add(removeOpt);
removeCmd.SetAction(HandleRemove);

var compatCmd = new Command("compat", "旧版兼容  --packageId --packageVersion --Language");
compatCmd.Add(packOld);
compatCmd.Add(verOld);
compatCmd.Add(langOld);
compatCmd.SetAction(HandleCompat);

var root = new RootCommand("NugetTranslation - 翻译 NuGet XML 文档");
root.Add(translateCmd);
root.Add(removeCmd);
root.Add(compatCmd);

return await root.Parse(args).InvokeAsync();

// ==================================================================
//  方法
// ==================================================================

async Task HandleTranslate(ParseResult r)
{
    var code = r.GetValue(codeOpt) ?? "zh-Hans";
    var packs = r.GetValue(packOpt);
    if (packs == null)
    {
        Console.Error.WriteLine("需要 -pack");
        return;
    }

    foreach (var item in packs.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        var at   = item.IndexOf('@');
        var id   = at < 0 ? item        : item[..at];
        var ver  = at < 0 ? "*"         : item[(at + 1)..];

        Console.WriteLine($"\n===== 翻译: {id}@{ver} =====");

        try
        {
            await Translator.Run(id, ver, code);
            Console.WriteLine($"  ✅ {id} 完成");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ {id}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

void HandleRemove(ParseResult r)
{
    var code  = r.GetValue(codeOpt) ?? "zh-Hans";
    var items = r.GetValue(removeOpt);
    if (items == null)
    {
        Console.Error.WriteLine("需要 -remove");
        return;
    }

    var cacheDir    = Path.GetFullPath(Path.Combine("..", "cache", code));
    var totalRemoved = 0;

    foreach (var item in items.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
    {
        var at  = item.IndexOf('@');
        if (at < 0)
        {
            Console.Error.WriteLine($"格式错误: {item}，应为 pkgId@memberName");
            continue;
        }

        var pkgId      = item[..at];
        var memberName = item[(at + 1)..];
        var dbPath     = Path.Combine(cacheDir, $"{pkgId.ToLower()}.db");

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"  ⚠️ 缓存不存在: {pkgId}");
            continue;
        }

        using var conn = new SqliteConnection($"Data Source={dbPath}");
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cache WHERE key LIKE @pattern";
        cmd.Parameters.AddWithValue("@pattern", $"%name=\"{memberName}\"%");
        var removed = cmd.ExecuteNonQuery();

        if (removed > 0)
        {
            totalRemoved += (int)removed;
            Console.WriteLine($"  🗑️ {pkgId}: 删除 {removed} 条匹配 \"{memberName}\"");
        }
        else
        {
            Console.WriteLine($"  - {pkgId}: 无匹配 \"{memberName}\"");
        }

        using var walCmd = conn.CreateCommand();
        walCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        walCmd.ExecuteNonQuery();
    }

    Console.WriteLine($"\n删除完成，共移除 {totalRemoved} 条缓存");
}

async Task HandleCompat(ParseResult r)
{
    var id   = r.GetValue(packOld);
    if (id == null)
    {
        Console.Error.WriteLine("需要 --packageId");
        return;
    }

    var ver  = r.GetValue(verOld)  ?? "*";
    var code = r.GetValue(langOld) ?? "zh-Hans";

    await Translator.Run(id, ver, code);
}
