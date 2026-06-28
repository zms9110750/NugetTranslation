using NugetTranslation;

// 工作目录
var probeDir = new DirectoryInfo(AppContext.BaseDirectory);
while (probeDir != null && !probeDir.GetFiles("NugetTranslation.csproj").Any())
    probeDir = probeDir.Parent;
if (probeDir != null)
    Environment.CurrentDirectory = probeDir.FullName;
Directory.CreateDirectory("packages");
Environment.CurrentDirectory = Path.Combine(Environment.CurrentDirectory, "packages");

var version = "*";
var language = "zh-Hans";

var packages = new[]
{
    "Autofac", "FluentFTP", "Markdig",
    "Microsoft.Extensions.Caching.Abstractions", "Microsoft.Extensions.Configuration",
    "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Hosting.Abstractions",
    "Microsoft.Extensions.Logging", "Microsoft.Extensions.Logging.Abstractions",
    "Microsoft.Extensions.Options", "Microsoft.Extensions.Primitives",
    "Nito.AsyncEx.Coordination", "Polly.Core", "Refit", "Serilog",
    "Serilog.Sinks.Console", "Serilog.Sinks.File", "SixLabors.ImageSharp",
    "System.Interactive", "System.Linq.Async", "YamlDotNet"
};

var sw = System.Diagnostics.Stopwatch.StartNew();
var tasks = packages.Select(pkg => RunOne(pkg, version, language));
await Task.WhenAll(tasks);

Console.WriteLine($"\n全部完成，耗时 {sw.Elapsed.TotalMinutes:F1} 分");

static async Task RunOne(string pkg, string ver, string lang)
{
    try
    {
        await Translator.Run(pkg, ver, lang);
        Console.WriteLine($"  ✅ {pkg}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ {pkg}: {ex.GetType().Name}");
    }
}
