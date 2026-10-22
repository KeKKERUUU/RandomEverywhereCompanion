using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.Core.Wizard;

/// <summary>
/// Owns route selection and forward-only reachability for every installer mode.
/// </summary>
public sealed class WizardState
{
    // Routes are explicit so each edition exposes only the operations it supports.
    private static readonly WizardStep[] FullInstallationRoute =
    [
        WizardStep.Disclaimer,
        WizardStep.Language,
        WizardStep.Simulator,
        WizardStep.ZipFolder,
        WizardStep.PackageCheck,
        WizardStep.Installation,
        WizardStep.Completed
    ];

    private static readonly WizardStep[] AndrewUpdateRoute =
    [
        WizardStep.Disclaimer,
        WizardStep.Language,
        WizardStep.Simulator,
        WizardStep.ZipFolder,
        WizardStep.Installation,
        WizardStep.Completed
    ];

    private int _currentIndex;
    private int _furthestReachedIndex;
    private bool _companionWorkflow;
    private static readonly WizardStep[] CompanionInstallRoute =
        [WizardStep.ZipFolder, WizardStep.PackageCheck, WizardStep.Installation, WizardStep.Completed];
    private static readonly WizardStep[] CompanionUpdateRoute =
        [WizardStep.ZipFolder, WizardStep.Installation, WizardStep.Completed];

    public InstallerMode Mode { get; private set; } = InstallerMode.FullInstallation;

    public WizardStep CurrentStep => Route[_currentIndex];

    public WizardStep FurthestReachedStep => Route[_furthestReachedIndex];

    public IReadOnlyList<WizardStep> Route => Mode switch
    {
        InstallerMode.FullInstallation => _companionWorkflow ? CompanionInstallRoute : FullInstallationRoute,
        InstallerMode.AndrewUpdateOnly => _companionWorkflow ? CompanionUpdateRoute : AndrewUpdateRoute,
        _ => throw new InvalidOperationException($"Unsupported installer mode: {Mode}")
    };

    public event EventHandler? Changed;

    public bool CanMoveNext => _currentIndex < Route.Count - 1;

    public bool CanMoveBack => _currentIndex > 0;

    // The shell has already handled consent, language and Community selection.
    // Remove those steps from reachability, rather than merely skipping past them.
    public void BeginCompanionWorkflow()
    {
        if (_companionWorkflow || CurrentStep != WizardStep.Disclaimer)
            throw new InvalidOperationException("A companion workflow requires a fresh wizard.");
        _companionWorkflow = true;
        _currentIndex = 0;
        _furthestReachedIndex = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool SetMode(InstallerMode mode)
    {
        if (Mode == mode) return false;
        if (CurrentStep != WizardStep.Disclaimer)
            throw new InvalidOperationException("Installation mode can only be changed on the disclaimer step.");

        Mode = mode;
        _currentIndex = 0;
        _furthestReachedIndex = 0;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool MoveNext()
    {
        if (!CanMoveNext)
        {
            return false;
        }

        _currentIndex++;
        if (_currentIndex > _furthestReachedIndex)
        {
            _furthestReachedIndex = _currentIndex;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool MoveBack()
    {
        if (!CanMoveBack)
        {
            return false;
        }

        _currentIndex--;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CanNavigateTo(WizardStep step)
    {
        var index = IndexOf(step);
        return index >= 0 && index <= _furthestReachedIndex;
    }

    public bool NavigateTo(WizardStep step)
    {
        if (!CanNavigateTo(step) || step == CurrentStep)
        {
            return false;
        }

        _currentIndex = IndexOf(step);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public int GetDisplayNumber(WizardStep step)
    {
        var index = IndexOf(step);
        return index < 0 ? 0 : index + (_companionWorkflow ? 1 : 0);
    }

    private int IndexOf(WizardStep step)
    {
        for (var index = 0; index < Route.Count; index++)
        {
            if (Route[index] == step) return index;
        }
        return -1;
    }
}
