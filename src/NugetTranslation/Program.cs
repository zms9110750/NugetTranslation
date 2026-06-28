using System.CommandLine;
using System.Reflection;
using NugetTranslation;

#if LOCAL
var probeDir = new DirectoryInfo(AppContext.BaseDirectory);
while (probeDir != null && !probeDir.GetFiles("NugetTranslation.csproj").Any())
    probeDir = probeDir.Parent;
if (probeDir != null)
    Environment.CurrentDirectory = probeDir.FullName;
#endif
Directory.CreateDirectory("packages");
Environment.CurrentDirectory = Path.Combine(Environment.CurrentDirectory, "packages");

var pack = new Option<string>("--packageId", "package", "-Package", "-p", "-pack", "Include");
var ver = new Option<string>("--packageVersion", "--version", "-v", "-Version", "Version");
var lange = new Option<string>("--Language", "--language", "-l", "-Language", "Language");
var command = new Command(Assembly.GetEntryAssembly()?.GetName().Name!) { pack, ver, lange };
ParseResult parseResult = command.Parse(args);
var packageId = parseResult.GetValue(pack);
if (packageId == null)
{
    throw new ArgumentNullException(nameof(packageId), "请提供包ID");
}
var packageVersion = parseResult.GetValue(ver) ?? "*";
var language = parseResult.GetValue(lange) ?? "zh-Hans";

await Translator.Run(packageId, packageVersion, language);
