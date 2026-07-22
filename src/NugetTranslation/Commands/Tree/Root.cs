using NugetTranslation.Commands.Tree.Cache;
using NugetTranslation.Commands.Tree.Translate;
using System.CommandLine;

namespace NugetTranslation.Commands.Tree;

internal static class Root
{
    public static readonly Argument<string> Code = new("code") { Description = "语言代码，如 zh-Hans" };

    public static readonly Option<string> Flags = new("--flags") {
        Description = "从 appsettings.json 选的配置名",
#if DEBUG
        DefaultValueFactory = _ => "local"
#else
        DefaultValueFactory = _ => "release"
#endif
    };

    public static RootCommand Create()
    {
        var root = new RootCommand("NugetTranslation - 翻译 NuGet XML 文档");
        root.Add(Code);
        root.Add(Flags);
        root.Add(TranslateCommand.Create());
        root.Add(CacheCommand.Create());
        return root;
    }
}
