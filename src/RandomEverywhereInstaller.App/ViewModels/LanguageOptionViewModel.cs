using RandomEverywhereInstaller.App.Infrastructure;
namespace RandomEverywhereInstaller.App.ViewModels;

public sealed class LanguageOptionViewModel(
    string culture,
    string nativeName,
    string flag,
    string note) : ObservableObject
{
    public string Culture { get; } = culture;
    public string NativeName { get; } = nativeName;
    public string Flag { get; } = flag;
    public string FlagImage => $"Assets/Flags/{Culture}.png";
    public string Note { get; } = note;
}
