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

        var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Instance = raw?
            .Where(kvp => !kvp.Key.StartsWith('$'))
            .ToDictionary(
                kvp => kvp.Key,
                kvp => JsonSerializer.Deserialize<ProfileConfig?>(kvp.Value.GetRawText(),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
                    }));
    }
}
