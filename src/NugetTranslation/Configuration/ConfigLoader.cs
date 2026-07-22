using System.Text.Json;

namespace NugetTranslation.Configuration;

internal static class ConfigLoader
{
    public static IReadOnlyDictionary<string, ProfileConfig?>? Instance { get => field ?? throw new ArgumentNullException(nameof(Instance)); private set; }

    public static void Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        if (!File.Exists(path))
            path = "appsettings.json";

        Instance = JsonSerializer.Deserialize<Dictionary<string, ProfileConfig?>>(
            File.ReadAllText(path),
            new JsonSerializerOptions {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });
    }
}
