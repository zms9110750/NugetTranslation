using NugetTranslation.Commands.Tree.Cache;
using NugetTranslation.Commands.Tree.Translate;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;

namespace NugetTranslation.Commands.Tree;

internal static class Root
{
    public static readonly Argument<string> Code = new("code")
    {
        Description = "语言/区域代码，如 zh-Hans、ja、ko"
    };

    static Root()
    {
        Code.Validators.Add(ValidateCode);
    }

    private static void ValidateCode(ArgumentResult result)
    {
        var value = result.Tokens.Single().Value;
        try
        {
            _ = CultureInfo.GetCultureInfo(value);
        }
        catch (CultureNotFoundException)
        {
            result.AddError($"'{value}' 不是有效的区域代码。请使用如 zh-Hans、ja、ko 等 RFC 5646 语言标签。");
        }
    }

    public static readonly Option<string> Profile = new("--profile", "-p") {
        Description = "选择 appsettings.json 中的配置 profile",
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
        root.Add(Profile);
        root.Add(TranslateCommand.Create());
        root.Add(CacheCommand.Create());
        return root;
    }
}
