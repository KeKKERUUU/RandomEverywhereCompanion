using System.Text.Json;
using RandomEverywhereInstaller.Core.Companion;

namespace RandomEverywhereInstaller.Infrastructure.Companion;

public sealed class CompanionSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public CompanionSettings Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<CompanionSettings>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    public void Save(CompanionSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, path, overwrite: true);
    }
}
