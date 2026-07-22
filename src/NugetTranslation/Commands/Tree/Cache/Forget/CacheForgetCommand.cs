using Microsoft.Data.Sqlite;
using System.CommandLine;

namespace NugetTranslation.Commands.Tree.Cache.Forget;

internal static class CacheForgetCommand
{
    public static readonly Argument<string[]> PackNameArg = new("pack-name") { Description = "pkgId@memberName，如 foo@MethodName" };

    public static Command Create()
    {
        var cmd = new Command("forget", "删除指定 member 的缓存");
        cmd.Add(PackNameArg);
        cmd.SetAction(Handle);
        return cmd;
    }

    static void Handle(ParseResult ctx)
    {
        var code = ctx.GetValue(Root.Code)!;
        var items = ctx.GetValue(PackNameArg);

        if (items is null || items.Length == 0)
        {
            Console.Error.WriteLine("需要 pack-name");
            return;
        }

        var cacheDir = Path.GetFullPath(Path.Combine("..", "cache", code));
        var totalRemoved = 0;

        foreach (var item in items)
        {
            var at = item.IndexOf('@');
            if (at < 0)
            {
                Console.Error.WriteLine($"格式错误: {item}，应为 pkgId@memberName");
                continue;
            }

            var pkgId = item[..at];
            var memberName = item[(at + 1)..];
            var dbPath = Path.Combine(cacheDir, $"{pkgId.ToLower()}.db");

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
}
