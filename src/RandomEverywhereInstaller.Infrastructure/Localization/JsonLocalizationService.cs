using System.Globalization;
using System.Text.Json;
using RandomEverywhereInstaller.Core.Localization;

namespace RandomEverywhereInstaller.Infrastructure.Localization;

public sealed class JsonLocalizationService : ILocalizationService
{
    private const string FallbackCulture = "en-US";
    private readonly Func<string, Stream> _openResource;
    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _current = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> _fallback = new Dictionary<string, string>();

    public JsonLocalizationService(Func<string, Stream> openResource, string initialCulture = FallbackCulture)
    {
        _openResource = openResource;
        SupportedCultures = ["en-US", "de-DE", "fr-FR", "es-ES", "it-IT"];
        _fallback = Load(FallbackCulture);
        SetCulture(initialCulture);
    }

    public string CurrentCulture { get; private set; } = FallbackCulture;

    public IReadOnlyList<string> SupportedCultures { get; }

    public event EventHandler? CultureChanged;

    public string GetText(string key, params object[] arguments)
    {
        if (!_current.TryGetValue(key, out var value) && !_fallback.TryGetValue(key, out value))
        {
            value = $"[{key}]";
        }

        return arguments.Length == 0
            ? value
            : string.Format(CultureInfo.GetCultureInfo(CurrentCulture), value, arguments);
    }

    public void SetCulture(string cultureName)
    {
        if (!SupportedCultures.Contains(cultureName, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentOutOfRangeException(nameof(cultureName), cultureName, "Unsupported culture.");
        }

        if (string.Equals(CurrentCulture, cultureName, StringComparison.OrdinalIgnoreCase) && _current.Count > 0)
        {
            return;
        }

        _current = Load(cultureName);
        CurrentCulture = cultureName;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
        CultureChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyDictionary<string, string> Load(string cultureName)
    {
        if (_cache.TryGetValue(cultureName, out var cached))
        {
            return cached;
        }

        using var stream = _openResource(cultureName);
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidDataException($"Localization resource '{cultureName}' is empty.");

        _cache[cultureName] = values;
        return values;
    }
}
