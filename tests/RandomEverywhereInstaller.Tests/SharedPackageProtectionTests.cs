using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Infrastructure.Installer;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static void SharedPackageProtectionPreservesKnownAndUnknownContent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var community = Path.Combine(root, "Community");
            var destination = Path.Combine(community, SharedPackageProtection.PackageName);
            var source = Path.Combine(root, "stage");
            Directory.CreateDirectory(destination);
            Directory.CreateDirectory(source);
            var canonical = "canonical HEMS";
            var older = "old vehicle";
            var paths = new[] { "canonical.bin", "upgrade.bin", "unknown.bin", "unverified.bin", "missing.bin" };
            object Fingerprint(string path, string content) => new { path, size = Encoding.UTF8.GetByteCount(content), sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))) };
            using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, package = SharedPackageProtection.PackageName,
                canonicalFiles = paths.Select(p => Fingerprint(p, canonical)).ToArray(),
                olderCounterparts = paths.Select(p => Fingerprint(p, older)).ToArray()
            }));
            var policy = new SharedPackageProtection(stream);
            File.WriteAllText(Path.Combine(destination, "canonical.bin"), canonical);
            File.WriteAllText(Path.Combine(source, "canonical.bin"), older);
            File.WriteAllText(Path.Combine(destination, "upgrade.bin"), older);
            File.WriteAllText(Path.Combine(source, "upgrade.bin"), canonical);
            File.WriteAllText(Path.Combine(destination, "unknown.bin"), "user's newer content");
            File.WriteAllText(Path.Combine(source, "unknown.bin"), canonical);
            File.WriteAllText(Path.Combine(destination, "unverified.bin"), older);
            File.WriteAllText(Path.Combine(source, "unverified.bin"), "unclassified incoming");
            File.WriteAllText(Path.Combine(destination, "unlisted.bin"), "user smoke effect");
            File.WriteAllText(Path.Combine(source, "unlisted.bin"), "overwrite attempt");
            File.WriteAllText(Path.Combine(source, "missing.bin"), canonical);
            var other = Path.Combine(community, "unrelated");
            Directory.CreateDirectory(other);
            File.WriteAllText(Path.Combine(other, "keep.txt"), "untouched");

            var transaction = new InstallTransaction(community, Path.Combine(root, "backup"), new InstallerLog(root), policy);
            transaction.MergePackage(source, SharedPackageProtection.PackageName);
            Equal(canonical, File.ReadAllText(Path.Combine(destination, "canonical.bin")));
            Equal(canonical, File.ReadAllText(Path.Combine(destination, "upgrade.bin")));
            Equal("user's newer content", File.ReadAllText(Path.Combine(destination, "unknown.bin")));
            Equal(older, File.ReadAllText(Path.Combine(destination, "unverified.bin")));
            Equal("user smoke effect", File.ReadAllText(Path.Combine(destination, "unlisted.bin")));
            Equal(canonical, File.ReadAllText(Path.Combine(destination, "missing.bin")));
            Equal("untouched", File.ReadAllText(Path.Combine(other, "keep.txt")));
            Equal(1, Directory.GetFiles(Path.Combine(root, "backup"), "*", SearchOption.AllDirectories).Length);
            True(transaction.Warnings.Single().StartsWith("Preserved 3 ", StringComparison.Ordinal), "Unknown or unverified overlaps must be reported in the completion warnings.");
            transaction.Rollback();
            Equal(older, File.ReadAllText(Path.Combine(destination, "upgrade.bin")));
            False(File.Exists(Path.Combine(destination, "missing.bin")), "Rollback removes only the new shared file.");
            Equal(canonical, File.ReadAllText(Path.Combine(destination, "canonical.bin")));
            var rejected = false;
            try { transaction.ReplacePackage(source, SharedPackageProtection.PackageName); }
            catch (InstallerException ex) when (ex.Code == "PKG-009") { rejected = true; }
            True(rejected, "Directory replacement must not bypass the shared-package guard.");
            Equal(350, SharedPackageProtection.Default.CanonicalCount);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void BundledSimConnectIsThePinnedX64Redistributable()
    {
        var path = Path.Combine(FindRepositoryRoot(), "src", "RandomEverywhereInstaller.CompanionRuntime", "Native", "win-x64", "SimConnect.dll");
        using var stream = File.OpenRead(path);
        Equal("BD15E8A094762BBDF32320F549B174340B8F1E58B63624DCC93BC50507E7B619", Convert.ToHexString(SHA256.HashData(stream)));
        Equal(79360L, stream.Length);
        stream.Position = 0;
        using var pe = new PEReader(stream);
        Equal(Machine.Amd64, pe.PEHeaders.CoffHeader.Machine);
        False(pe.HasMetadata, "Bundle the native x64 DLL, not the managed SDK wrapper.");
    }
}
