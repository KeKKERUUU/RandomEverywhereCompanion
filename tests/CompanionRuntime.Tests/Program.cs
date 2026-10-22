using System;
using System.IO;
using HPGMissionCompanion;

internal static class Program
{
    private static int checks;
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "RECompanionTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.wav"), "not played by tests");
            var xml = Path.Combine(root, "SoundMappings.xml");
            File.WriteAllText(xml, "<SoundList category=\"Test\"><Sound LocalVar=\"CPLAND1\" FileName=\"hello,missing\"/></SoundList>");
            var pack = RecordedAudio.Load(xml);
            Require(pack.Name == "Test", "Pack category");
            Require(pack.Mappings.Count == 1, "Playable mapping retained");
            Require(pack.Mappings[0].Variable == "L:CPLAND1", "Local variable normalization");
            Require(pack.Mappings[0].Files.Count == 1 && pack.Warnings.Count == 1, "Missing variants are warnings");
            File.WriteAllText(xml, "<SoundList><Sound LocalVar=\"X\" FileName=\"../outside.wav\"/></SoundList>");
            pack = RecordedAudio.Load(xml);
            Require(pack.Mappings.Count == 0 && pack.Warnings.Count == 1, "Traversal rejected");
            var settings = new Settings { AudioFolder = "user-audio", WorkFolder = "user-work" };
            settings.AudioOut.HeadsetDevice = "user-device";
            Profiles.Apply(Profiles.Recommended(), settings);
            Require(settings.AudioFolder == "user-audio" && settings.WorkFolder == "user-work" && settings.AudioOut.HeadsetDevice == "user-device", "Profiles preserve machine-specific paths and outputs");
            int major, minor;
            Require(VersionCompatibility.ParseVersion("v0.19", out major, out minor) && major == 0 && minor == 19, "Protocol versions use integer components");
            File.WriteAllText(xml, "<!DOCTYPE voicepack [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><voicepack name='Untrusted' version='1'>&x;</voicepack>");
            Require(!VoicePacks.Load(xml).Usable, "External XML entities are rejected");
            var escaped = false;
            try { RuntimeFileSafety.ManagedPath(root, "../outside"); }
            catch (InvalidDataException) { escaped = true; }
            Require(escaped, "Managed runtime paths reject traversal");
            Require(!SimLink.Connected, "Simulator connection is false before a real session");
            RecordedPlaybackTests.Run(root, Require);
            RecordedLibraryTests.Run(root, Require);
            SyntheticEngineTests.Run(root, Require);
            RuntimePayloadTests.Run(Require);
            Console.WriteLine("All " + checks + " runtime/audio checks passed. No audio device, simulator or UI was started.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static void Require(bool value, string name)
    {
        if (!value) throw new Exception(name);
        checks++;
        Console.WriteLine("PASS " + name);
    }
}
