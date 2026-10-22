using System.Security.Cryptography;
using System.Text.Json;

namespace RandomEverywhereInstaller.Infrastructure.Installer;

/// <summary>Prevents known country overlays from downgrading shared HEMS objects.</summary>
internal sealed class SharedPackageProtection
{
    public const string PackageName = "68ponygt-actionpack-std-objects";
    private static readonly Lazy<SharedPackageProtection> Embedded = new(LoadEmbedded);
    private readonly Dictionary<string, Fingerprint> _canonical = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Fingerprint>> _older = new(StringComparer.OrdinalIgnoreCase);
    public static SharedPackageProtection Default => Embedded.Value;
    internal int CanonicalCount => _canonical.Count;

    internal SharedPackageProtection(Stream json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("schema").GetInt32() != 1 || root.GetProperty("package").GetString() != PackageName)
            throw new InvalidDataException("Unsupported shared-package fingerprint.");
        foreach (var entry in root.GetProperty("canonicalFiles").EnumerateArray())
        {
            var (path, fingerprint) = Parse(entry);
            _canonical.Add(path, fingerprint);
        }
        foreach (var entry in root.GetProperty("olderCounterparts").EnumerateArray())
        {
            var (path, fingerprint) = Parse(entry);
            if (!_canonical.ContainsKey(path)) throw new InvalidDataException("An older fingerprint has no canonical counterpart.");
            if (!_older.TryGetValue(path, out var list)) _older[path] = list = [];
            list.Add(fingerprint);
        }
        if (_canonical.Count == 0) throw new InvalidDataException("Shared-package fingerprint is empty.");
    }

    public bool ShouldCopy(string relative, string incoming, string installed, out string reason)
    {
        reason = "missing-file";
        if (!File.Exists(installed)) return true;
        relative = relative.Replace('\\', '/');
        if (!_canonical.TryGetValue(relative, out var canonical))
        {
            reason = "preserve-unlisted-installed-content";
            return false;
        }
        var installedSize = new FileInfo(installed).Length;
        var candidates = _older.TryGetValue(relative, out var old) ? old : [];
        if (installedSize != canonical.Size && !candidates.Any(item => item.Size == installedSize))
        {
            reason = "preserve-unknown-installed-content";
            return false;
        }
        var installedHash = Hash(installed);
        if (installedHash.Equals(canonical.Hash, StringComparison.OrdinalIgnoreCase))
        {
            reason = "preserve-canonical-installed-content";
            return false;
        }
        if (!candidates.Any(item => item.Size == installedSize && item.Hash.Equals(installedHash, StringComparison.OrdinalIgnoreCase)))
        {
            reason = "preserve-unknown-installed-content";
            return false;
        }
        if (new FileInfo(incoming).Length == canonical.Size && Hash(incoming).Equals(canonical.Hash, StringComparison.OrdinalIgnoreCase))
        {
            reason = "upgrade-known-older-to-canonical";
            return true;
        }
        reason = "incoming-is-not-a-verified-upgrade";
        return false;
    }

    private static SharedPackageProtection LoadEmbedded()
    {
        using var stream = typeof(SharedPackageProtection).Assembly.GetManifestResourceStream("SharedPackageFingerprints.std-objects.json")
            ?? throw new InvalidDataException("Shared-package protection data is missing.");
        return new SharedPackageProtection(stream);
    }

    private static (string Path, Fingerprint Value) Parse(JsonElement element)
    {
        var path = element.GetProperty("path").GetString()!.Replace('\\', '/');
        var size = element.GetProperty("size").GetInt64();
        var hash = element.GetProperty("sha256").GetString()!;
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.Contains(':') ||
            path.Split('/').Any(segment => segment is ".." or "." or "") || size < 0 || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid shared-package fingerprint entry.");
        return (path, new Fingerprint(size, hash));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private sealed record Fingerprint(long Size, string Hash);
}
