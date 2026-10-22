using System.Text.RegularExpressions;
using RandomEverywhereInstaller.Core.Installer;

namespace RandomEverywhereInstaller.Infrastructure.Installer;

/// <summary>
/// Matches configured names first, then tolerates user-specific download suffixes.
/// </summary>
internal static partial class PackageNameMatcher
{
    public static bool IsMatch(PackageDefinition definition, string path)
    {
        var fileName = Path.GetFileName(path);
        var configured = new Regex(definition.FileNamePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (configured.IsMatch(fileName)) return true;

        // Token matching ignores punctuation, versions, and random download identifiers.
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var normalized = SeparatorsRegex().Replace(stem, " ").Trim();
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var compact = normalized.Replace(" ", string.Empty, StringComparison.Ordinal);

        bool Has(params string[] values) => values.All(value => compact.Contains(value, StringComparison.OrdinalIgnoreCase));
        bool Token(params string[] values) => values.Any(tokens.Contains);

        return definition.Id switch
        {
            "FR_VEHICLES" => Has("pack") && (Has("vehicles") || Has("vehicules")) && Token("fr", "french", "france"),
            "30WEST_HEMS" => Has("30west", "hems", "objects"),
            "30WEST_SMOKE" => Has("30west", "smoke"),
            "DE_VEHICLES" => Has("action", "pack", "vehicles") && Token("de", "german", "germany"),
            "IT_VEHICLES" => Has("action", "pack", "vehicles") && Token("it", "italian", "italy"),
            "US_VEHICLES" => Has("action", "pack", "vehicles") && Token("us", "usa", "american"),
            "NL_VEHICLES" => Has("action", "pack", "vehicles") && Token("nl", "dutch", "netherlands"),
            "UK_VEHICLES" => Has("action", "pack", "vehicles") && Token("uk", "british"),
            "ANDREW_MISSION_PACK" => Has("h145", "andrew", "mission", "pack"),
            "BUNDLE_VOICE_PACK" => Has("bundle", "voice", "pack"),
            "DUSTIN_VOICE_PACK" => Has("h145", "dustin", "voice", "pack"),
            "H145_HEMS_OBJECTS" => Has("hems", "objects") && (Has("h145", "action", "pack") || Has("68ponygt")),
            _ => false
        };
    }

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorsRegex();
}
