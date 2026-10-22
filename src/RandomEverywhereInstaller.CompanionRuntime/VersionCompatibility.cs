using System;
using System.Text.RegularExpressions;

namespace HPGMissionCompanion
{
    // Pack compatibility uses the upstream protocol version, not the companion release.
    internal static class VersionCompatibility
    {
        public static bool ParseVersion(string text, out int major, out int minor)
        {
            major = 0; minor = 0;
            if (string.IsNullOrEmpty(text)) return false;
            var match = Regex.Match(text, "(\\d+)\\.(\\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out major) &&
                int.TryParse(match.Groups[2].Value, out minor);
        }
    }
}
