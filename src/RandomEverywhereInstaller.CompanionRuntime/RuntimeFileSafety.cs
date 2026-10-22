using System;
using System.IO;

namespace HPGMissionCompanion
{
    // Runtime writes stay under managed data roots and never follow junctions.
    internal static class RuntimeFileSafety
    {
        public static bool HasReparsePoint(string path)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return true;
            var directory = Directory.Exists(path) ? new DirectoryInfo(path) : new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path)));
            for (; directory != null; directory = directory.Parent)
                if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return true;
            return false;
        }

        public static string ManagedPath(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0)
                throw new InvalidDataException("Invalid managed file path.");
            var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var path = Path.GetFullPath(Path.Combine(prefix, relative));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || HasReparsePoint(path))
                throw new InvalidDataException("A runtime path escapes its storage root or follows a link.");
            return path;
        }
    }
}
