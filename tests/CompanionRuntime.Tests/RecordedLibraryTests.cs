using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using System.Xml;
using HPGMissionCompanion;

internal static class RecordedLibraryTests
{
    public static void Run(string root, Action<bool, string> require)
    {
        var community = Path.Combine(root, "library-community");
        var folder = Path.Combine(community, "nested", "recorded", "audio");
        var external = Path.Combine(root, "external-recordings");
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(external);
        var audio = Path.Combine(folder, "greeting.WAV");
        File.WriteAllText(audio, "Synthetic fixture; never decoded or played.");
        File.WriteAllText(Path.Combine(folder, "my-packtest.wav"), "Preview fixture.");
        var xml = Path.Combine(folder, "author-custom-name.xml");
        File.WriteAllText(xml, "<SoundList category='Original'>" +
            "<Sound LocalVar='VOICE' FileName='greeting,greeting.WAV,missing,'/>" +
            "<Sound LocalVar='TEST' FileName='my-packtest'/>" +
            "<Sound LocalVar='MISSING' FileName='not-here.wav'/>" +
            "<Sound LocalVar='VOICE' FileName='greeting'/></SoundList>");
        var unrelated = Path.Combine(community, "unrelated.xml");
        File.WriteAllText(unrelated, "<unrelated>Not a sound pack</unrelated>");
        var externalXml = Path.Combine(external, "different-name.xml");
        File.WriteAllText(Path.Combine(external, "one.mp3"), "Not played.");
        File.WriteAllText(externalXml, "<SoundList category='Original'><Sound LocalVar='ONE' FileName='one'/></SoundList>");
        var before = Directory.GetFiles(community, "*", SearchOption.AllDirectories).Concat(Directory.GetFiles(external)).ToDictionary(p => p, Hash);
        var registry = new RecordedPackRegistry();

        RecordedAudio.Scan(community, registry);
        require(RecordedAudio.Packs.Count == 1 && RecordedAudio.Packs[0].File == xml,
            "Nested recorded XML is discovered by content, not filename");
        var pack = RecordedAudio.Packs[0];
        require(pack.AllMappings.Count == 4 && pack.Mappings.Count == 3 && pack.AllMappings[2].Problems.Count == 1,
            "Unavailable mappings remain visible but cannot be played");
        require(pack.Mappings[0].Files.Count == 2 && pack.Mappings[0].Problems.Count == 1,
            "Repeated variants retain author weighting and missing variants are diagnosed");
        require(RecordedAudio.FindTestMapping(pack).Variable == "L:TEST",
            "Pack preview recognizes packtest in the filename like the standalone manager");
        require(RecordedAudio.ResolvePreview("recorded", pack, 3).Variable == "L:VOICE",
            "Individual preview uses the stable mapping row, including repeated variables");
        require(Rejects(() => RecordedAudio.ResolvePreview("recorded", pack, 2)) &&
            Rejects(() => RecordedAudio.ResolvePreview("recorded", pack, 999)),
            "Preview cannot bypass missing files or supply an arbitrary path");
        require(Rejects(() => RecordedAudio.ResolvePreview("synthetic", pack, 0)),
            "Recorded preview is rejected in synthetic mode without opening an output");

        registry.Register(new[] { externalXml, externalXml.ToUpperInvariant() });
        RecordedAudio.Scan(community, registry);
        require(registry.Files.Count == 1 && RecordedAudio.Packs.Count == 2,
            "External registrations are deduplicated by path, not category name");
        registry.Register(new[] { xml });
        RecordedAudio.Scan(community, registry);
        require(RecordedAudio.Packs.Count == 2, "Community and external discovery cannot duplicate the same XML");

        registry.Forget(xml);
        RecordedAudio.Scan(community, registry);
        require(RecordedAudio.Packs.Count == 1 && RecordedAudio.Packs[0].File == externalXml && File.Exists(xml),
            "Unregister hides the selected entry without deleting Community content");
        registry.Register(new[] { xml });
        RecordedAudio.Scan(community, registry);
        require(RecordedAudio.Packs.Count == 2 && registry.Hidden.Count == 0, "Explicit re-registration restores a hidden pack");

        var serializer = new JavaScriptSerializer();
        var preferences = new RuntimePreferences { RecordedLibrary = registry, RecordedPack = xml, RecordedVolume = 37, RecordedMasterMuted = true };
        var restored = serializer.Deserialize<RuntimePreferences>(serializer.Serialize(preferences));
        require(restored.RecordedLibrary.Files.Count == 2 && restored.RecordedPack == xml && restored.RecordedVolume == 37 && restored.RecordedMasterMuted,
            "Registry, selected pack, retained level and mute round-trip independently");

        var tts = Path.Combine(external, "synthetic.xml");
        File.WriteAllText(tts, "<voicepack name='TTS' version='1'><message>hello</message></voicepack>");
        var fresh = new RecordedPackRegistry();
        require(Rejects(() => fresh.Register(new[] { xml, tts })) && fresh.Files.Count == 0,
            "Mixed recorded/TTS batches are rejected atomically");
        require(Rejects(() => fresh.Register(new[] { audio })) && fresh.Files.Count == 0,
            "Registration requires XML content rather than loose media or archives");
        var dtd = Path.Combine(external, "dtd.xml");
        File.WriteAllText(dtd, "<!DOCTYPE SoundList [<!ENTITY file SYSTEM 'file:///C:/Windows/win.ini'>]><SoundList>&file;</SoundList>");
        require(Rejects(() => fresh.Register(new[] { dtd })) && fresh.Files.Count == 0,
            "Recorded XML prohibits external entities and DTD expansion");
        var corrupt = Path.Combine(external, "corrupt.xml");
        File.WriteAllText(corrupt, "<SoundList><Sound");
        require(Rejects(() => fresh.Register(new[] { xml, corrupt })) && fresh.Files.Count == 0,
            "Malformed recorded XML cannot partially register a batch");

        var unsafeXml = Path.Combine(external, "unsafe.xml");
        File.WriteAllText(unsafeXml, "<SoundList><Sound LocalVar='X' FileName='../outside.wav,C:\\outside.wav,one.mp3:stream'/></SoundList>");
        var unsafePack = RecordedAudio.Load(unsafeXml);
        require(unsafePack.Mappings.Count == 0 && unsafePack.AllMappings.Count == 1 && unsafePack.Warnings.Count == 3,
            "Traversal, absolute paths and alternate streams are diagnosed and unavailable");
        registry.Files.Add(Path.Combine(external, "removed.xml"));
        RecordedAudio.Scan(community, registry);
        require(RecordedAudio.Packs.Count == 2 && RecordedAudio.DiscoveryWarnings.Any(p => p.Contains("removed.xml")),
            "Missing external registrations are visible without breaking valid packs");
        require(before.All(p => File.Exists(p.Key) && p.Value.SequenceEqual(Hash(p.Key))),
            "Scan, register, reload and unregister preserve all source-file hashes");
        File.WriteAllText(Path.Combine(folder, "not-here.wav"), "Added by the fixture author.");
        RecordedAudio.Scan(community, registry);
        require(RecordedAudio.Packs.Single(p => p.File == xml).Mappings.Count == 4,
            "Reload recognizes newly supplied recordings without reimporting XML");

        var muted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        require(!RecordedAudio.TriggeredMappings(pack, "L:VOICE", 0, 1, true, muted).Any() &&
            !RecordedAudio.TriggeredMappings(pack, "L:VOICE", double.NaN, 1, false, muted).Any() &&
            !RecordedAudio.TriggeredMappings(pack, "L:VOICE", 1, double.PositiveInfinity, false, muted).Any(),
            "Initial baselines and non-finite counters never trigger recordings");
        require(RecordedAudio.TriggeredMappings(pack, "l:voice", 1, 2, false, muted).Count() == 2 &&
            !RecordedAudio.TriggeredMappings(pack, "L:VOICE", 2, 1, false, muted).Any() &&
            !RecordedAudio.TriggeredMappings(pack, "L:VOICE", 1, 1, false, muted).Any(),
            "Rising counters trigger every matching mapping, never resets or repeats");
        muted.Add("l:voice");
        require(!RecordedAudio.TriggeredMappings(pack, "L:VOICE", 1, 2, false, muted).Any(),
            "Per-variable mute suppresses all variants without changing their files");

        var previous = Ctx.Settings;
        var italian = Path.Combine(root, "ItalianPackage");
        var soundFolder = Path.Combine(italian, "Simobjects", "Airplanes", "hpg-airbus-h145-ems", "Sound");
        Directory.CreateDirectory(soundFolder);
        File.WriteAllText(Path.Combine(italian, "manifest.json"), "{}");
        File.WriteAllText(Path.Combine(soundFolder, "ITA.HW1.wav"), "Test fixture, not played.");
        var italianXml = Path.Combine(italian, "SoundMappings.xml");
        File.WriteAllText(italianXml, "<SoundList category='ITALIAN'><Sound LocalVar='APPHW1' FileName='ITA.HW1'/></SoundList>");
        require(RecordedAudio.Load(italianXml).Mappings.Single().Files.Single().Equals(Path.Combine(soundFolder, "ITA.HW1.wav"), StringComparison.OrdinalIgnoreCase),
            "Root-level Italian XML resolves WAV inside the same package's aircraft Sound folder");
        var secondSound = Path.Combine(italian, "SimObjects", "Airplanes", "another-aircraft", "Sound");
        Directory.CreateDirectory(secondSound);
        File.WriteAllText(Path.Combine(secondSound, "ITA.HW1.wav"), "Different recording.");
        var ambiguous = RecordedAudio.Load(italianXml);
        require(ambiguous.Mappings.Count == 0 && ambiguous.Warnings.Any(p => p.Contains("Ambiguous")),
            "Ambiguous package fallback cannot select the wrong recording silently");
        File.WriteAllText(Path.Combine(italian, "ITA.HW1.wav"), "Author's direct reference.");
        require(RecordedAudio.Load(italianXml).Mappings.Single().Files.Single() == Path.Combine(italian, "ITA.HW1.wav"),
            "XML-local audio has priority over all package fallback folders");
        var noManifest = Path.Combine(root, "NoPackage");
        Directory.CreateDirectory(noManifest);
        var orphan = Path.Combine(noManifest, "map.xml");
        File.WriteAllText(orphan, File.ReadAllText(italianXml));
        require(RecordedAudio.Load(orphan).Mappings.Count == 0, "Fallback never searches a sibling package for missing audio");
        File.WriteAllText(Path.Combine(external, "unsupported.ogg"), "Not decodable.");
        File.WriteAllText(Path.Combine(external, "ogg.xml"), "<SoundList><Sound LocalVar='OGG' FileName='unsupported'/></SoundList>");
        var ogg = RecordedAudio.Load(Path.Combine(external, "ogg.xml"));
        require(ogg.Mappings.Count == 0 && ogg.AllMappings.Count == 1 && ogg.Warnings.Any(p => p.Contains("Unsupported recorded format")),
            "OGG is reported unsupported before playback and never marked playable");
        Ctx.Settings = new Settings { MasterVolume = 63 };
        try
        {
            RecordedAudio.SetVolume(37); RecordedAudio.SetMuted(true);
            require(RecordedAudio.Volume == 37 && RecordedAudio.MasterMuted, "Mute retains the exact recorded master level");
            RecordedAudio.SetVolume(42); RecordedAudio.SetMuted(false);
            require(RecordedAudio.Volume == 42 && !RecordedAudio.MasterMuted && Ctx.Settings.MasterVolume == 63,
                "Unmute restores the chosen level and leaves synthetic master unchanged");
        }
        finally { Ctx.Settings = previous; RecordedAudio.SetMuted(false); RecordedAudio.SetVolume(80); }
        var state = typeof(Worker).GetMethod("Snapshot", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).Invoke(null, new object[] { false });
        var status = serializer.DeserializeObject(serializer.Serialize(state)) as Dictionary<string, object>;
        require(status.ContainsKey("Running") && status.ContainsKey("RecordedLastPlayed") && !status.ContainsKey("RecordedPacks"),
            "Status polling excludes the library and does not enumerate audio devices");
    }

    private static bool Rejects(Action action)
    {
        try { action(); return false; }
        catch (InvalidDataException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (XmlException) { return true; }
    }
    private static byte[] Hash(string path)
    { using (var hash = SHA256.Create()) using (var stream = File.OpenRead(path)) return hash.ComputeHash(stream); }
}
