using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HPGMissionCompanion;

internal static class SyntheticEngineTests
{
    public static void Run(string root, Action<bool, string> check)
    {
        var settings = Ctx.Settings;
        var packs = VoicePacks.Packs;
        var baseDir = Ctx.BaseDir;
        var audioDir = Ctx.AudioDir;
        try
        {
            RunRegressions(root, check);
            Ctx.BaseDir = root;
            Ctx.AudioDir = Path.Combine(root, "synthetic-effects");
            Directory.CreateDirectory(Ctx.AudioDir);
            Ctx.Settings = new Settings();
            Say.ResetSession();
            var enPath = Path.Combine(root, "EN.xml");
            using (var stream = typeof(Say).Assembly.GetManifestResourceStream("VoicePacks.EN.xml"))
            using (var output = File.Create(enPath)) stream.CopyTo(output);
            var english = VoicePacks.Load(enPath);
            check(AppInfo.Version == "0.28" && english.Usable && english.Version == 16, "Pinned synthetic engine 0.28 and EN16 are embedded");
            check(english.Messages.ContainsKey("DSP.NAME.MAN") && english.Messages.Values.Any(m => m.Actor == Actors.HeliRescuer), "EN16 dispatcher and rescuer metadata are present");
            check(Actors.Count == 12 && Actors.Checklist == 11 && Actors.HeliRescuer == 12, "Actor 12 does not replace checklist channel 11");

            var pack = new VoicePack { Name = "TEST", Version = 1 };
            pack.Messages["CREW.TEST"] = new PackMessage { Key = "CREW.TEST", Actor = 4, Radio = "off", Options = new List<string> { "Ready" } };
            pack.Messages["TECH.TEST"] = new PackMessage { Key = "TECH.TEST", Actor = 6, Radio = "off", Options = new List<string> { "Technician" } };
            pack.Messages["WAV.TEST"] = new PackMessage { Key = "WAV.TEST", Actor = 4, Wav = "unchanged.wav" };
            VoicePacks.Packs = new List<VoicePack> { pack };
            Say.SetBridgePack("TEST");
            string wav;
            var notes = new List<string>();
            var line = Say.Process("<mission:RE><dsp:2:Rome><defaultactor:11>Ready", notes, out wav);
            check(Say.MissionId == "RE" && Say.DispatcherIndex == 2 && Say.DispatcherCity == "Rome", "Mission and dispatcher state tags are retained outside the spoken text");
            check(line == "<actor11>Ready", "Control tags do not leak into spoken text");
            Say.Process("<defaultactor:off>", notes, out wav);
            Say.OnWatch(SimLink.CtxExist, 0, 7, true);
            line = Say.Process("<say:CREW.TEST>", notes, out wav);
            check(line.StartsWith("<actor3>", StringComparison.Ordinal), "Synthetic absent rear crew uses the doctor stand-in");
            Say.OnWatch(SimLink.CtxMute, 0, 4, true);
            line = Say.Process("<say:CREW.TEST>", notes, out wav);
            check(line.StartsWith("<actor2>", StringComparison.Ordinal), "Stand-in skips a muted doctor");
            Say.OnWatch(SimLink.CtxMute, 4, 7, true);
            check(Say.Process("<say:CREW.TEST>", notes, out wav) == "", "No eligible synthetic crew produces no speech");
            Say.Process("<say:WAV.TEST>", notes, out wav);
            check(wav == "unchanged.wav", "Synthetic stand-in never rewrites sound-file messages");
            Say.OnWatch(SimLink.CtxTechSex, 0, 1, true);
            line = Say.Process("<say:TECH.TEST>", notes, out wav);
            check(line.StartsWith("<actor7>", StringComparison.Ordinal), "Technician speech follows the scene's female technician");
            Say.Process("<defaultactor:11>", notes, out wav);
            Say.ResetSession();
            check(Say.MissionId == "" && Say.DispatcherIndex == -2 && Say.Process("Ready", notes, out wav) == "Ready", "Session reset clears identity, dispatcher, crew and default actor");

            var voices = new List<VoiceInfo> {
                new VoiceInfo { Id = "test:fixed", Display = "Fixed - male", Family = "en", Keys = new string[0] },
                new VoiceInfo { Id = "test:a", Display = "A - female", Family = "en", Keys = new string[0] },
                new VoiceInfo { Id = "test:b", Display = "B - male", Family = "en", Keys = new string[0] },
                new VoiceInfo { Id = "test:c", Display = "C - male", Family = "en", Keys = new string[0] }
            };
            var lang = LangInfo.ByCode("EN");
            Ctx.Settings.Get("EN", 1).Voice = "test:fixed";
            foreach (var actor in new[] { 8, 9, 12 }) Ctx.Settings.Get("EN", actor).Random = true;
            var drawn = new[] { 8, 9, 12 }.Select(actor => VoiceDraw.Pick(Ctx.Settings, lang, actor, voices)).ToArray();
            check(drawn.Distinct().Count() == 3 && !drawn.Contains("test:fixed"), "Random synthetic actors avoid fixed and already drawn voices");
            var previous = VoiceDraw.Current("EN", 8);
            check(previous == VoiceDraw.Pick(Ctx.Settings, lang, 8, voices), "Drawn synthetic voices remain stable during a shift");
            Ctx.Settings.Get("EN", 8).Pool = new List<string> { "test:a" };
            VoiceDraw.Reset();
            check(VoiceDraw.Pick(Ctx.Settings, lang, 8, voices) == "test:a", "Explicit random voice pools are respected");
            var female = Resolver.Resolve(new Segment { Actor = 7, Lang = "EN" }, Ctx.Settings, voices);
            check(female.Voice.Id == "test:a", "Unconfigured technician voice prefers the correct gender");
            check(Say.BuildCallSign("I-TEST", "PELIKAN 3", "999") == "Pelikan 3" && Say.BuildCallSign("I-TEST", "", "999") == "", "Call sign follows the livery airline, not invented flight numbers");

            var profile = Profiles.Parse("{\"Schema\":1,\"CallSignMode\":\"custom\",\"CallSign\":\"Rescue 2\",\"Languages\":{\"EN\":{\"Actors\":[]}}}");
            Ctx.Settings.AudioFolder = "recordings-untouched";
            Profiles.Apply(profile, Ctx.Settings);
            check(Ctx.Settings.CallSignMode == "custom" && Ctx.Settings.CallSign == "Rescue 2" && Ctx.Settings.AudioFolder == "recordings-untouched", "Synthetic profiles preserve recording location and custom call sign");
            check(Ctx.Settings.Get("EN", 12) != null, "Older actor profiles expand safely to twelve actors");

            AppSounds.MissionSoundRoot = Path.Combine(root, "mission-notifications");
            Directory.CreateDirectory(AppSounds.MissionSoundRoot);
            var effect = "AND.NEW.MSG.wav";
            File.WriteAllBytes(Path.Combine(AppSounds.MissionSoundRoot, effect), new byte[] { 1, 2, 3, 4 });
            check(AppSounds.Provide(Path.Combine(Ctx.AudioDir, effect)) && File.ReadAllBytes(Path.Combine(Ctx.AudioDir, effect)).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Mission notification effects are copied byte-for-byte without playback");
            File.WriteAllText(Path.Combine(Ctx.AudioDir, effect), "user custom");
            check(AppSounds.Provide(Path.Combine(Ctx.AudioDir, effect)) && File.ReadAllText(Path.Combine(Ctx.AudioDir, effect)) == "user custom", "Existing user notification overrides are not overwritten");
            File.WriteAllText(Path.Combine(AppSounds.MissionSoundRoot, "crew-voice.wav"), "recording");
            check(!AppSounds.Provide(Path.Combine(Ctx.AudioDir, "crew-voice.wav")), "Synthetic notification preparation cannot import recorded voice clips");
            check(!AppSounds.Provide(Path.Combine(root, effect)), "Synthetic notification destinations cannot escape their audio directory");
            check(Ctx.Player == null && !SimLink.Connected, "Synthetic regression tests never start playback or the simulator");
        }
        finally
        {
            Say.ResetSession();
            Ctx.Settings = settings; Ctx.BaseDir = baseDir; Ctx.AudioDir = audioDir;
            VoicePacks.Packs = packs; AppSounds.MissionSoundRoot = null;
        }
    }

    static void RunRegressions(string root, Action<bool, string> check)
    {
        var failures = new List<string>();
        Action<bool, string> verify = (ok, name) =>
        {
            if (ok) check(true, name);
            else { failures.Add(name); Console.WriteLine("FAIL " + name); }
        };
        foreach (var regression in new Action[] {
            () => ServerDispatchRegression(verify),
            () => NullActorRegression(root, verify),
            () => PreferredPackRegression(verify),
            () => MappedCounterRegression(verify) })
        {
            try { regression(); }
            catch (Exception ex) { failures.Add(ex.ToString()); Console.WriteLine("FAIL " + ex); }
        }
        check(failures.Count == 0, "Isolated synthetic regressions: " + string.Join("; ", failures));
    }

    static void PreferredPackRegression(Action<bool, string> check)
    {
        var oldSettings = Ctx.Settings;
        var oldPacks = VoicePacks.Packs;
        var oldWatch = SimLink.OnWatch;
        var oldAtc = SimLink.OnAtcStrings;
        try
        {
            Say.ResetSession();
            var invalid = new VoicePack { Name = "BROKEN", Version = 0 };
            var german = new VoicePack { Name = "DE", Language = "DE", Version = 1 };
            var english = new VoicePack { Name = "EN", Language = "EN", Version = 1 };
            var custom = new VoicePack { Name = "CUSTOM", Language = "IT", Version = 1 };
            VoicePacks.Packs = new List<VoicePack> { invalid, german, english, custom };
            Ctx.Settings = new Settings();
            Ctx.Settings.Normalize();
            Say.ApplySettings();
            check(Say.BridgePackName == "EN", "Default English pack beats alphabetically earlier German pack");

            Ctx.Settings.BridgePack = "removed-pack";
            Say.ApplySettings();
            check(Say.BridgePackName == "EN" && Ctx.Settings.BridgePack == "removed-pack",
                "Missing saved pack falls back without rewriting the user's preference");
            Ctx.Settings.BridgePack = "BROKEN";
            Say.ApplySettings();
            check(Say.BridgePackName == "EN", "Unusable saved text pack cannot become the active bridge pack");
            Ctx.Settings.BridgePack = "dE";
            Say.ApplySettings();
            check(Say.BridgePackName == "DE", "Explicit usable text pack wins case-insensitively");

            Ctx.Settings.BridgePack = "";
            Ctx.Settings.DefaultLanguage = "DE";
            Say.ApplySettings();
            check(Say.BridgePackName == "DE", "Default voice language chooses its available text pack");
            string wav;
            Say.Process("<pack:CUSTOM:1>", new List<string>(), out wav);
            Say.ApplySettings();
            check(Say.BridgePackName == "CUSTOM", "Mission-declared usable pack is the first automatic fallback");
            Ctx.Settings.BridgePack = "en";
            Say.ApplySettings();
            check(Say.BridgePackName == "EN", "Explicit pack keeps priority over the mission declaration");

            Say.ResetSession();
            Ctx.Settings.BridgePack = "";
            Ctx.Settings.DefaultLanguage = "FR";
            Say.ApplySettings();
            check(Say.BridgePackName == "EN", "Missing default-language pack falls back to English");
            VoicePacks.Packs = new List<VoicePack> { invalid, custom };
            Say.ApplySettings();
            check(Say.BridgePackName == "CUSTOM", "Last-resort automatic choice skips unusable text packs");
            VoicePacks.Packs = new List<VoicePack> { invalid };
            Say.ApplySettings();
            check(Say.BridgePackName == "", "No usable text pack clears the active bridge pack");
            check(Ctx.Player == null && !SimLink.Connected,
                "Pack-selection regressions do not open audio or a simulator session");
        }
        finally
        {
            Say.ResetSession(); Ctx.Settings = oldSettings; VoicePacks.Packs = oldPacks;
            SimLink.OnWatch = oldWatch; SimLink.OnAtcStrings = oldAtc;
        }
    }

    static void ServerDispatchRegression(Action<bool, string> check)
    {
        var server = new Server { AllowFileMessages = false };
        var events = new List<string>();
        var logs = new List<string>();
        var oldLog = Ctx.Log;
        Ctx.Log = logs.Add;
        try
        {
            Action<string> dispatch = raw => server.Handle(raw,
                (text, interrupt) => events.Add("text:" + text + ":" + interrupt),
                (path, interrupt) => events.Add("file:" + path), () => events.Add("cancel"));
            dispatch("{\"Text\":\"Ready\"}");
            dispatch("{\"Text\":\"Null\",\"FileName\":null}");
            dispatch("{\"Text\":\"Empty\",\"FileName\":\"\",\"Interrupt\":true}");
            dispatch("{\"Interrupt\":true}");
            check(events.SequenceEqual(new[] { "text:Ready:False", "text:Null:False", "text:Empty:True", "cancel" }),
                "Synthetic protocol accepts absent/null/empty filenames and interrupt-only commands");
            check(!logs.Any(line => line.StartsWith("[ERROR]", StringComparison.Ordinal)),
                "Valid synthetic protocol messages produce no dispatch errors");
            events.Clear();
            dispatch("{\"FileName\":\"recording.wav\",\"Interrupt\":true}");
            dispatch("{\"Text\":\"Do not speak\",\"FileName\":\"recording.wav\"}");
            check(events.Count == 0, "Synthetic mode rejects file requests before any speech or cancellation");
        }
        finally { Ctx.Log = oldLog; }
    }

    static void NullActorRegression(string root, Action<bool, string> check)
    {
        var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
        var settings = new Settings { MasterVolume = 63, AudioFolder = "recordings-untouched" };
        settings.Normalize();
        var before = serializer.Serialize(settings);
        var rejected = false;
        try
        {
            var profile = Profiles.Parse("{\"Schema\":1,\"MasterVolume\":17,\"Languages\":{\"EN\":{\"Actors\":[null]}}}");
            Profiles.Apply(profile, settings);
        }
        catch (InvalidDataException) { rejected = true; }
        catch (NullReferenceException) { }
        check(rejected && serializer.Serialize(settings) == before,
            "Null profile actors are rejected before any destination settings change");

        var invalid = new ProfileData { MasterVolume = 17 };
        invalid.Languages["EN"] = new LangSetting { Actors = new List<ActorSetting> { null } };
        rejected = false;
        try { Profiles.Apply(invalid, settings); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected && serializer.Serialize(settings) == before,
            "Direct profile application also validates null actors before mutation");

        var path = Path.Combine(root, "null-actor-settings.json");
        File.WriteAllText(path, "{\"MasterVolume\":63,\"Languages\":{\"EN\":{\"Actors\":[null,{\"Voice\":\"keep-second\",\"Speed\":1.25}]}}}");
        var loaded = Settings.Load(path);
        check(loaded.MasterVolume == 63 && loaded.Get("EN", 1) != null &&
            loaded.Get("EN", 2).Voice == "keep-second" && loaded.Get("EN", 2).Speed == 1.25,
            "Settings loading repairs null actors in place without losing neighboring assignments");
        loaded.Normalize();
        check(loaded.Get("EN", 12) != null, "Null actor repair remains safe on repeated normalization");

        var legacy = new ProfileData();
        legacy.Languages["EN"] = new LangSetting { Actors = Enumerable.Range(1, 11)
            .Select(index => new ActorSetting { Voice = "actor-" + index, Speed = 1.25 }).ToList() };
        Profiles.Apply(Profiles.Parse(Profiles.ToJson(legacy)), settings);
        check(Enumerable.Range(1, 11).All(index => settings.Get("EN", index).Voice == "actor-" + index &&
            settings.Get("EN", index).Speed == 1.25) && settings.Get("EN", 12) != null,
            "Eleven-actor profiles preserve every assignment and append actor twelve");
    }

    static void MappedCounterRegression(Action<bool, string> check)
    {
        var oldPacks = VoicePacks.Packs;
        var oldSink = Say.Sink;
        var delivered = new List<string>();
        try
        {
            Say.ResetSession();
            var pack = new VoicePack { Name = "COUNTERS", Version = 1, Vcp = "APP" };
            pack.Messages["COUNTER.TEXT"] = new PackMessage { Key = "COUNTER.TEXT", Lvar = "L:TEST{VCP}1",
                Actor = 1, Radio = "off", Options = new List<string> { "Counter ready" } };
            pack.Messages["COUNTER.SOUND"] = new PackMessage { Key = "COUNTER.SOUND", Lvar = "L:TEST{VCP}2",
                Wav = "unchanged.wav", Sfx = true };
            VoicePacks.Packs = new List<VoicePack> { pack };
            Say.Sink = delivered.Add;
            Say.SetBridgePack(pack.Name);
            var watches = Say.WatchNames();
            check(watches.Contains("L:TESTAPP1") && watches.Contains("L:TESTAPP2"),
                "Synthetic bridge registers expanded text and sound counter mappings");
            Say.OnWatch("L:TESTAPP1", 0, 1, true);
            Say.OnWatch("L:TESTAPP1", 1, 1, false);
            Say.OnWatch("L:TESTAPP1", 1, 0, false);
            Say.OnWatch("L:UNMAPPED", 0, 1, false);
            check(delivered.Count == 0, "Mapped bridge suppresses baseline/reset/repeat and unmapped counters");
            Say.OnWatch("L:TESTAPP1", 0, 1, false);
            check(delivered.Count == 1 && delivered[0].Contains("Counter ready"),
                "Mapped rising counter delivers exactly one synthetic line through Sink");
            Say.OnWatch("L:TESTAPP2", 0, 1, false);
            check(delivered.Count == 2 && delivered[1].Contains("[sound file unchanged.wav]"),
                "Mapped sound-only counter preserves the filename without speech substitution");
            delivered.Clear();
            foreach (var invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
            {
                Say.OnWatch("L:TESTAPP1", 0, invalid, false);
                Say.OnWatch("L:TESTAPP1", invalid, 1, false);
                Say.OnWatch("L:TESTAPP2", 0, invalid, false);
                Say.OnWatch("L:TESTAPP2", invalid, 1, false);
            }
            check(delivered.Count == 0, "Non-finite synthetic counters never deliver text or sound messages");
            delivered.Clear();
            Say.OnWatch("L:TESTAPP1", 2, 3, false);
            check(delivered.Count == 1 && delivered[0].Contains("Counter ready"),
                "Finite rising synthetic counters remain usable after invalid samples");
            check(Ctx.Player == null && !SimLink.Connected,
                "Protocol and mapped-counter regressions require neither Player nor a simulator session");
        }
        finally { Say.ResetSession(); Say.Sink = oldSink; VoicePacks.Packs = oldPacks; }
    }
}
