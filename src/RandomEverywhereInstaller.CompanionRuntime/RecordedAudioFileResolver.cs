using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HPGMissionCompanion
{
    // Resolves author layouts without searching another package or rewriting media.
    internal sealed class RecordedAudioFileResolver
    {
        readonly string xmlRoot;
        List<string> soundFolders;

        public RecordedAudioFileResolver(string xmlFile)
        {
            xmlRoot = Path.GetFullPath(Path.GetDirectoryName(xmlFile)).TrimEnd('\\') + "\\";
        }

        public string Resolve(string name)
        {
            var relative = name.Trim().Replace('/', '\\');
            if (Path.IsPathRooted(relative) || relative.Contains(":")) throw new InvalidDataException("Unsafe audio path: " + name);
            var path = Path.GetFullPath(Path.Combine(xmlRoot, relative));
            if (!path.StartsWith(xmlRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe audio path: " + name);
            var direct = FindFile(path);
            if (direct != null) return ValidateFile(direct);

            // Only bare filenames use the package's declared aircraft Sound folders.
            if (Path.GetFileName(relative) != relative) return null;
            if (soundFolders == null) soundFolders = FindSoundFolders();
            var matches = soundFolders.Select(folder => FindFile(Path.Combine(folder, relative)))
                .Where(file => file != null).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (matches.Length > 1) throw new InvalidDataException("Ambiguous recorded filename; use an explicit relative path: " + name);
            return matches.Length == 1 ? ValidateFile(matches[0]) : null;
        }

        static string FindFile(string path)
        {
            return new[] { path, path + ".wav", path + ".mp3", path + ".ogg" }.FirstOrDefault(File.Exists);
        }

        static string ValidateFile(string path)
        {
            if (RuntimeFileSafety.HasReparsePoint(path)) throw new InvalidDataException("Linked audio files are not supported: " + Path.GetFileName(path));
            if (Path.GetExtension(path).Equals(".ogg", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported recorded format (.ogg): " + Path.GetFileName(path) + ". Supply WAV or MP3; the app does not transcode or substitute speech.");
            return path;
        }

        List<string> FindSoundFolders()
        {
            var result = new List<string>();
            var directory = new DirectoryInfo(xmlRoot);
            for (var depth = 0; directory != null && depth < 16; depth++, directory = directory.Parent)
            {
                if (RuntimeFileSafety.HasReparsePoint(directory.FullName)) return result;
                // The nearest manifest defines the only package boundary we may use.
                if (File.Exists(Path.Combine(directory.FullName, "manifest.json")))
                {
                    var airplanes = Path.Combine(directory.FullName, "SimObjects", "Airplanes");
                    if (!Directory.Exists(airplanes) || RuntimeFileSafety.HasReparsePoint(airplanes)) return result;
                    foreach (var aircraft in Directory.EnumerateDirectories(airplanes))
                    {
                        var sound = Path.Combine(aircraft, "Sound");
                        if (Directory.Exists(sound) && !RuntimeFileSafety.HasReparsePoint(sound)) result.Add(sound);
                    }
                    return result;
                }
                if (directory.Name.Equals("Community", StringComparison.OrdinalIgnoreCase)) break;
            }
            return result;
        }
    }
}
