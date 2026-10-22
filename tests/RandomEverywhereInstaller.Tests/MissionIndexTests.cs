using System.Text;
using System.Text.Json;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static string IndexTarget(string community) => Path.Combine(community, "hpg-airbus-h145", "html_ui", "HPGH145-System", "GeneratedMissionIndex.json");
    private static string IndexMission(string community, string package, string relative, string content)
    {
        var file = Path.Combine(community, package, "hpgmission", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content, new UTF8Encoding(false));
        return file;
    }
    private static void NativeIndexStructure()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "hpg-airbus-h145"));
            IndexMission(root, "a-pack", "Shared/one.json", "{\"id\":\"one\"}");
            IndexMission(root, "b-pack", "shared/two.JSON", "{\"id\":\"two\"}");
            IndexMission(root, "a-pack", "Shared/Nested/three.json", "{\"id\":\"three\"}");
            IndexMission(root, "a-pack", "root.json", "{\"id\":\"root\"}");
            var ignored = IndexMission(root, "a-pack", "Shared/notes.txt", "not a mission");
            var sources = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
            Equal(4, MissionIndexGenerator.Generate(root, CancellationToken.None));
            using var index = JsonDocument.Parse(File.ReadAllBytes(IndexTarget(root)));
            var missions = index.RootElement.GetProperty("hpgmission");
            Equal(2, missions.GetProperty("Shared").GetProperty("*").GetArrayLength());
            Equal(1, missions.GetProperty("Shared").GetProperty("Nested").GetProperty("*").GetArrayLength());
            Equal(1, missions.GetProperty("*").GetArrayLength());
            var ids = missions.GetProperty("Shared").GetProperty("*").EnumerateArray().Select(e =>
            {
                using var json = JsonDocument.Parse(Convert.FromBase64String(e.GetString()!));
                return json.RootElement.GetProperty("id").GetString();
            }).ToArray();
            True(ids.SequenceEqual(["one", "two"]), "Merged missions lost stable package order or payloads.");
            foreach (var (file, bytes) in sources) True(File.ReadAllBytes(file).SequenceEqual(bytes), "Source changed: " + file);
            Equal("not a mission", File.ReadAllText(ignored));
            var first = File.ReadAllBytes(IndexTarget(root));
            MissionIndexGenerator.Generate(root, CancellationToken.None);
            True(first.SequenceEqual(File.ReadAllBytes(IndexTarget(root))), "Unchanged inputs must produce stable index bytes.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void NativeIndexUnicode()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "hpg-airbus-h145"));
            const string text = "é è 中文 🚁";
            var json = "{\n\"title\":\"" + text + "\",\"literal\":\"a\\nb\",\"n\":1.25\n}";
            var file = IndexMission(root, "pack", "mission.json", json);
            File.WriteAllText(file, json, Encoding.Unicode); // UTF-16 BOM from legacy Windows tooling.
            Equal(1, MissionIndexGenerator.Generate(root, CancellationToken.None));
            using var index = JsonDocument.Parse(File.ReadAllBytes(IndexTarget(root)));
            var bytes = Convert.FromBase64String(index.RootElement.GetProperty("hpgmission").GetProperty("*")[0].GetString()!);
            True(bytes.All(b => b < 128), "Payload must be safe for legacy JavaScript atob without UTF-8 decoding.");
            using var payload = JsonDocument.Parse(Encoding.ASCII.GetString(bytes));
            Equal(text, payload.RootElement.GetProperty("title").GetString());
            Equal("a\nb", payload.RootElement.GetProperty("literal").GetString());
            Equal(1.25, payload.RootElement.GetProperty("n").GetDouble());
        }
        finally { Directory.Delete(root, true); }
    }

    private static void NativeIndexFailureAtomicity()
    {
        foreach (var exists in new[] { false, true })
        {
            var root = CreateTemporaryDirectory();
            try
            {
                var target = IndexTarget(root);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (exists) File.WriteAllText(target, "{\"keep\":\"original\"}");
                IndexMission(root, "pack", "a-valid.json", "{}");
                IndexMission(root, "pack", "z-invalid.json", "{bad");
                try { MissionIndexGenerator.Generate(root, CancellationToken.None); throw new InvalidOperationException("Invalid JSON accepted."); }
                catch (InstallerException error) when (error.Code == "INDEX-JSON") { True(error.Message.Contains("z-invalid.json"), "Failure must identify the bad source."); }
                Equal(exists, File.Exists(target));
                if (exists) Equal("{\"keep\":\"original\"}", File.ReadAllText(target));
                False(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "A failed index left temporary files.");
            }
            finally { Directory.Delete(root, true); }
        }
    }

    private static void NativeIndexCancellation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var target = IndexTarget(root);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, "{\"original\":true}");
            IndexMission(root, "pack", "mission.json", "{}");
            using var cancel = new CancellationTokenSource();
            try { MissionIndexGenerator.Generate(root, cancel.Token, (_, _) => cancel.Cancel()); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            Equal("{\"original\":true}", File.ReadAllText(target));
            False(Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(), "Cancelled index left temporary files.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void NativeIndexLinks()
    {
        var root = CreateTemporaryDirectory();
        var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(root, "Outside")).FullName;
        var link = Path.Combine(community, "linked");
        try
        {
            Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145"));
            var sentinel = IndexMission(outside, "untouched", "mission.json", "{\"private\":true}");
            CreateTestJunction(link, outside);
            try { MissionIndexGenerator.Generate(community, CancellationToken.None); throw new InvalidOperationException("A package junction was followed."); }
            catch (InstallerException error) when (error.Code == "INDEX-LINK") { }
            Equal("{\"private\":true}", File.ReadAllText(sentinel));
            False(File.Exists(IndexTarget(community)), "Linked input produced a partial index.");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(root, true);
        }
    }

    private static void NativeIndexDoesNotExecuteScript()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var tools = Directory.CreateDirectory(Path.Combine(root, "hpg-airbus-h145", "Tools")).FullName;
            var marker = Path.Combine(root, "script-executed.txt");
            File.WriteAllText(Path.Combine(tools, "Update Mission Index.cmd"), $"@echo forbidden>\"{marker}\"\r\n@exit /b 7\r\n");
            Equal(0, MissionIndexGenerator.Generate(root, CancellationToken.None));
            Equal("{}", File.ReadAllText(IndexTarget(root)));
            False(File.Exists(marker), "Legacy script was executed.");
            File.Delete(Path.Combine(tools, "Update Mission Index.cmd"));
            IndexMission(root, "pack", "mission.json", "{\"ok\":true}");
            Equal(1, MissionIndexGenerator.Generate(root, CancellationToken.None));
        }
        finally { Directory.Delete(root, true); }
    }
}
