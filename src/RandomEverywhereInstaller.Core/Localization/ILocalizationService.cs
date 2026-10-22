namespace RandomEverywhereInstaller.Core.Localization;

public interface ILocalizationService
{
    string CurrentCulture { get; }

    IReadOnlyList<string> SupportedCultures { get; }

    event EventHandler? CultureChanged;

    string GetText(string key, params object[] arguments);

    void SetCulture(string cultureName);
}
