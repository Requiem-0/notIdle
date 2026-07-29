using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotIdle.Core;

/// <summary>Source-generated metadata so JSON works under NativeAOT with no reflection.</summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Settings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>
/// Persists <see cref="Settings"/> to a single JSON file under %APPDATA%.
/// Loads defensively: any failure yields defaults rather than throwing.
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _path;

    public JsonSettingsStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    public static string DefaultPath()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NotIdle");
        return Path.Combine(dir, "settings.json");
    }

    public Settings Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Settings();
            using FileStream fs = File.OpenRead(_path);
            Settings? s = JsonSerializer.Deserialize(fs, SettingsJsonContext.Default.Settings);
            if (s is null) return new Settings();
            s.Normalize();
            return s;
        }
        catch
        {
            return new Settings(); // Corrupt/locked file must never wedge startup.
        }
    }

    public void Save(Settings settings)
    {
        settings.Normalize();
        string? dir = Path.GetDirectoryName(_path);
        if (dir is not null) Directory.CreateDirectory(dir);

        // Atomic-ish write: temp then move, so a crash mid-write can't corrupt the file.
        string tmp = _path + ".tmp";
        using (FileStream fs = File.Create(tmp))
        {
            JsonSerializer.Serialize(fs, settings, SettingsJsonContext.Default.Settings);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
