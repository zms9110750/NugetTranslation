using Microsoft.Extensions.DependencyInjection;
using System.CommandLine;
using ZiggyCreatures.Caching.Fusion;

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

    static async Task Handle(ParseResult ctx)
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

            // 安全检查：空白或通配符 * 跳过
            if (string.IsNullOrWhiteSpace(memberName) || memberName == "*")
            {
                Console.WriteLine($"  ⛔ 跳过: {item}（name 为空白或通配符，禁止删除）");
                continue;
            }

            var dbPath = Path.Combine(cacheDir, $"{pkgId.ToLower()}.db");

            if (!File.Exists(dbPath))
            {
                Console.WriteLine($"  ⚠️ 缓存不存在: {pkgId}");
                continue;
            }

            // 使用 FusionCache tag 删除（而非原始 SQL）
            var services = new ServiceCollection();
            Startup.AddFusionCacheAndSqliteCache(services, dbPath);
            using var sp = services.BuildServiceProvider();
            var cache = sp.GetRequiredService<IFusionCache>();

            await cache.RemoveByTagAsync(memberName);
            Console.WriteLine($"  🗑️ {pkgId}: 已按 tag \"{memberName}\" 删除");
            totalRemoved++;
        }

        Console.WriteLine($"\n删除完成，共处理 {totalRemoved} 条");
    }
}
