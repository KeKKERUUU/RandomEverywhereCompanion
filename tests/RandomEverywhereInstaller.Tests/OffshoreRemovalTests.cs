using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static void OffshoreRemovedWithoutDeletion()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Directory.CreateDirectory(Path.Combine(root, "Community")).FullName;
            Directory.CreateDirectory(Path.Combine(community, "hpg-airbus-h145"));
            var offshore = Directory.CreateDirectory(Path.Combine(community, "68ponygt-actionpack-vehicles-offshore")).FullName;
            File.WriteAllText(Path.Combine(offshore, "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(offshore, "layout.json"), "{\"content\":[]}");
            File.WriteAllText(Path.Combine(offshore, "user.txt"), "preserve");
            var mission = Directory.CreateDirectory(Path.Combine(offshore, "hpgmission")).FullName;
            File.WriteAllText(Path.Combine(mission, "existing.json"), "{\"title\":\"Existing user mission\"}");
            var snapshot = Directory.GetFiles(offshore, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
            var archive = Path.Combine(root, "Offshore Action Pack Objects v1.2_RANDOM.zip");
            CreateZip(archive, new Dictionary<string, string>
            {
                ["68ponygt-actionpack-vehicles-offshore/manifest.json"] = "{}",
                ["68ponygt-actionpack-vehicles-offshore/layout.json"] = "{}"
            });
            using var engine = new InstallerEngine();
            var inventory = engine.InspectCommunityPackages(community, SimulatorType.Msfs2024);
            Equal(12, inventory.Count);
            Equal(4, inventory.Count(p => p.Definition.Required));
            False(inventory.Any(p => p.Definition.Id == "OFFSHORE_OBJECTS" || p.Roots.Any(r => r.FolderName.Contains("offshore", StringComparison.OrdinalIgnoreCase))),
                "Offshore must not be a managed package.");
            var scan = engine.ScanPackagesAsync([archive], SimulatorType.Msfs2024, AuditProgress, CancellationToken.None).GetAwaiter().GetResult();
            False(scan.Any(p => p.ArchivePath == archive), "Offshore archive must not be selected for installation.");
            Equal(0, engine.RefreshMissionFilesAsync(community, AuditProgress, CancellationToken.None).GetAwaiter().GetResult());
            foreach (var (file, bytes) in snapshot) True(File.ReadAllBytes(file).SequenceEqual(bytes), "Catalog removal modified existing Offshore data.");
            Equal(snapshot.Count, Directory.GetFiles(offshore, "*", SearchOption.AllDirectories).Length);
        }
        finally { Directory.Delete(root, true); }
    }
}
