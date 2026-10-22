using System.Text.Json;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static void AndrewTitleReleaseVersions()
    {
        var newer = MissionReleaseVersion.FromTitle("HEMS RANDOM AND EVERYWHERE MISSIONS 0.997 169.45")!;
        var older = MissionReleaseVersion.FromTitle("HEMS RANDOM AND EVERYWHERE MISSIONS 0.997 169.9")!;
        Equal("0.997 169.45", newer.Display);
        True(MissionReleaseVersion.Compare(newer, older) > 0, "Release parts must be integers, not decimal fractions.");
        True(MissionReleaseVersion.Compare(newer, new("0.99634", "999.99", "BETA")) > 0, "Mission family must use decimal ordering.");
        Equal("0.997 0.0", MissionReleaseVersion.FromTitle("HEMS RANDOM AND EVERYWHERE MISSIONS 0.997")?.Display);
        Equal(newer, MissionReleaseVersion.FromTag("v0.997-169.45"));
        True(MissionReleaseVersion.FromTitle("MISSIONS 0.997 999999999999.1") is null, "Overflowed revisions must not be accepted.");
        True(MissionReleaseVersion.FromTitle("unrelated") is null, "An unrelated title is not a mission version.");
    }

    private static void AndrewTitleInspection()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "everywhere_all.json");
            using var engine = new InstallerEngine();
            File.WriteAllText(path, JsonSerializer.Serialize(new { id = "HEMSRandE", title = "HEMS RANDOM AND EVERYWHERE MISSIONS 0.997 169.45" }));
            Equal("0.997 169.45", engine.InspectAndrewUpdate(path).Version?.Display);
            File.WriteAllText(path, JsonSerializer.Serialize(new { id = "OTHER", title = "HEMS RANDOM AND EVERYWHERE MISSIONS 0.997 169.45" }));
            try { engine.InspectAndrewUpdate(path); throw new InvalidOperationException("Foreign mission identity was accepted."); }
            catch (InstallerException e) when (e.Code == "ANDREW-002") { }
            File.WriteAllText(path, CreateAndrewJson("0.99634", "1.3", "BETA"));
            Equal("0.99634 1.3 BETA", engine.InspectAndrewUpdate(path).Version?.Display);
        }
        finally { Directory.Delete(root, true); }
    }
}
