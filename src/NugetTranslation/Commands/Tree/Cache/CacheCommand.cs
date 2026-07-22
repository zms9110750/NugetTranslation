using NugetTranslation.Commands.Tree.Cache.Build;
using NugetTranslation.Commands.Tree.Cache.Forget;
using System.CommandLine;

namespace NugetTranslation.Commands.Tree.Cache;

internal static class CacheCommand
{
    public static Command Create()
    {
        var cmd = new Command("cache", "缓存操作");
        cmd.Add(CacheBuildCommand.Create());
        cmd.Add(CacheForgetCommand.Create());
        return cmd;
    }
}
