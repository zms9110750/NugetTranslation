using System.CommandLine;
using NugetTranslation.Commands.Tree;
using NugetTranslation.Configuration;

ConfigLoader.Load();

var root = Root.Create();
var parseResult = root.Parse(args);

// === 决定工作目录 ===
var flags = parseResult.GetValue(Root.Profile);
string? baseDir = null;

if (!string.IsNullOrEmpty(flags))
{
    var config = ConfigLoader.Instance;
    if (config?.TryGetValue(flags, out var profile) == true)
        baseDir = profile?.BaseDir;
    else
    {
        Console.Error.WriteLine($"未知配置: \"{flags}\"，请检查 appsettings.json");
        return 1;
    }
}

if (!string.IsNullOrEmpty(baseDir))
{
    Environment.CurrentDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, baseDir));
}

Directory.CreateDirectory("packages");
Environment.CurrentDirectory = Path.Combine(Environment.CurrentDirectory, "packages");

return await parseResult.InvokeAsync();
