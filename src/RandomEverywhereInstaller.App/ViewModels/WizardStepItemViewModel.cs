using RandomEverywhereInstaller.App.Infrastructure;
using RandomEverywhereInstaller.Core.Localization;
using RandomEverywhereInstaller.Core.Wizard;

namespace RandomEverywhereInstaller.App.ViewModels;

public sealed class WizardStepItemViewModel(
    WizardStep step,
    int number,
    string titleKey,
    string descriptionKey,
    ILocalizationService localization) : ObservableObject
{
    private bool _isActive;
    private bool _isComplete;
    private bool _isEnabled;

    public WizardStep Step { get; } = step;
    public int Number { get; } = number;
    public string Title => localization.GetText(titleKey);
    public string Description => localization.GetText(descriptionKey);
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }
    public bool IsComplete { get => _isComplete; set => SetProperty(ref _isComplete, value); }
    public bool IsEnabled { get => _isEnabled; set => SetProperty(ref _isEnabled, value); }
    public void RefreshText() => OnPropertiesChanged(nameof(Title), nameof(Description));
}
