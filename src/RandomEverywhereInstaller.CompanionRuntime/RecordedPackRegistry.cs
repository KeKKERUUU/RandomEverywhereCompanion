using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HPGMissionCompanion
{
    // Stores references only. Pack XML and recordings always remain user-owned.
    internal sealed class RecordedPackRegistry
    {
        public List<string> Files = new List<string>();
        public List<string> Hidden = new List<string>();

        public void Register(IEnumerable<string> paths)
        {
            var incoming = paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (incoming.Count == 0 || incoming.Count > 128) throw new InvalidDataException("Select between 1 and 128 recorded XML files.");
            // Validate the whole batch before changing any registration.
            foreach (var file in incoming)
            {
                if (!Path.GetExtension(file).Equals(".xml", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Recorded packs require a SoundList XML file, not an archive or a text voice pack.");
                var pack = RecordedAudio.Load(file);
                if (pack == null || pack.AllMappings.Count == 0)
                    throw new InvalidDataException("No recorded SoundList mappings found in " + Path.GetFileName(file) + ".");
            }
            foreach (var file in incoming)
            {
                if (!Files.Contains(file, StringComparer.OrdinalIgnoreCase)) Files.Add(file);
                Hidden.RemoveAll(p => p.Equals(file, StringComparison.OrdinalIgnoreCase));
            }
        }

        public void Forget(string file)
        {
            file = Path.GetFullPath(file);
            Files.RemoveAll(p => p.Equals(file, StringComparison.OrdinalIgnoreCase));
            if (!Hidden.Contains(file, StringComparer.OrdinalIgnoreCase)) Hidden.Add(file);
        }
    }
}
