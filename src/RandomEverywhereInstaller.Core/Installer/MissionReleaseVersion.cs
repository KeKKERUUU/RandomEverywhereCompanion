using System.Globalization;
using System.Text.RegularExpressions;

namespace RandomEverywhereInstaller.Core.Installer;

/// <summary>Upstream mission title contract: decimal family and integer release components.</summary>
public static class MissionReleaseVersion
{
    public const string MissionId = "HEMSRandE";
    private static readonly Regex Title = new(@"MISSIONS\s+(\d+(?:\.\d+)?)(?:\s+(\d+)\.(\d+))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Tag = new(@"(\d+\.\d+)\D+(\d+)\.(\d+)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static AndrewUpdateVersion? FromTitle(string? title) => Parse(title, Title, allowOriginal: true);
    public static AndrewUpdateVersion? FromTag(string? tag) => Parse(tag, Tag, allowOriginal: false);

    private static AndrewUpdateVersion? Parse(string? text, Regex pattern, bool allowOriginal)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 2048) return null;
        var match = pattern.Match(text);
        if (!match.Success || !decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out _)) return null;
        if (!match.Groups[2].Success) return allowOriginal ? new(match.Groups[1].Value, "0.0", null) : null;
        if (!int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return null;
        return new(match.Groups[1].Value, $"{major}.{minor}", null);
    }

    public static int Compare(AndrewUpdateVersion left, AndrewUpdateVersion right)
    {
        // Mission families are decimals (0.997 > 0.99634); releases are not (169.45 > 169.9).
        var family = decimal.TryParse(left.MissionVersion, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(right.MissionVersion, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var b)
            ? a.CompareTo(b) : CompareComponents(left.MissionVersion, right.MissionVersion);
        return family != 0 ? family : CompareComponents(left.DevVersion, right.DevVersion);
    }

    private static int CompareComponents(string left, string right)
    {
        var a = left.Split('.').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        var b = right.Split('.').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var comparison = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (comparison != 0) return comparison;
        }
        return 0;
    }
}
