using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using RandomEverywhereInstaller.App.ViewModels;
using RandomEverywhereInstaller.App.Services;
using RandomEverywhereInstaller.Core.Localization;
using RandomEverywhereInstaller.Core.Wizard;
using RandomEverywhereInstaller.Infrastructure.Installer;
using RandomEverywhereInstaller.Infrastructure.Localization;
using RandomEverywhereInstaller.Infrastructure.Updates;
using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.App;

public partial class App : Application
{
    private bool _applicationStarted;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        try
        {
            if (ApplicationUpdateBootstrapper.IsApplyRequest(e.Args))
            {
                var exitCode = ApplicationUpdateBootstrapper.ApplyAndRestart(e.Args);
                if (exitCode != 0)
                {
                    MessageBox.Show(
                        $"The application update could not be applied. The current executable was preserved whenever replacement did not complete.\n\n" +
                        $"Diagnostic log:\n{ApplicationUpdateBootstrapper.FailureLogPath}",
                        "Update error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
                Shutdown(exitCode);
                return;
            }

            ApplicationUpdateBootstrapper.CleanupPreviousExecutable();
            StartApplication(e.Args);
        }
        catch (Exception exception)
        {
            ReportFatalStartupError(exception);
            Shutdown(1);
        }
    }

    private void StartApplication(IReadOnlyCollection<string> arguments)
    {
        var localization = CreateLocalization();
        var engine = new InstallerEngine(AppEdition.RuntimeDirectoryName);
        var updateViewModel = CreateInstaller(localization, engine);
        var companion = new CompanionViewModel(localization, engine, updateViewModel);
        var window = new CompanionWindow(companion);
        updateViewModel.ExitRequested += (_, _) => window.CloseAfterUpdate();
        companion.InstallerRequested += async mode =>
        {
            var viewModel = CreateInstaller(localization, engine);
            try
            {
                await viewModel.PrepareCompanionWorkflowAsync(companion.CommunityPath,
                    companion.IsMsfs2020 ? SimulatorType.Msfs2020 : SimulatorType.Msfs2024, mode);
                if (companion.IsDisposed) return;
                await window.ShowInstallerAsync(viewModel);
            }
            catch (Exception exception)
            {
                MessageBox.Show(exception.Message, AppEdition.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { viewModel.Dispose(); }
        };
        MainWindow = window;
        companion.MissionUpdateReady += async file =>
        {
            using var viewModel = CreateInstaller(localization, engine);
            await viewModel.PrepareCompanionWorkflowAsync(companion.CommunityPath,
                companion.IsMsfs2020 ? SimulatorType.Msfs2020 : SimulatorType.Msfs2024, InstallerMode.AndrewUpdateOnly);
            await viewModel.ReviewDownloadedMissionAsync(file);
            if (!companion.IsDisposed) await window.ShowInstallerAsync(viewModel);
        };
        window.Closed += (_, _) => { updateViewModel.Dispose(); engine.Dispose(); };
        window.Show();
        _applicationStarted = true;
    }

    private static JsonLocalizationService CreateLocalization()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return new JsonLocalizationService(culture => assembly.GetManifestResourceStream(
            $"RandomEverywhereInstaller.App.Localization.{culture}.json")
            ?? throw new InvalidOperationException($"Missing localization resource: {culture}"));
    }

    private static MainWindowViewModel CreateInstaller(ILocalizationService localization, InstallerEngine engine) =>
        new(localization, new WizardState(), new WindowsFolderPickerService(),
            new WindowsDialogService(localization), engine, new GitHubReleaseUpdateService(), ownsEngine: false);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportFatalStartupError(e.Exception, !_applicationStarted);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteStartupErrorLog(exception, !_applicationStarted);
        }
    }

    private static void ReportFatalStartupError(Exception exception, bool startup = true)
    {
        var logPath = WriteStartupErrorLog(exception, startup);
        MessageBox.Show(
            (startup ? $"{AppEdition.ProductName} could not start.\n\n" : $"{AppEdition.ProductName} encountered an unexpected runtime error.\n\n") +
            $"A diagnostic log was saved to:\n{logPath}\n\n" +
            $"Error: {exception.Message}",
            startup ? "Startup error" : "Unexpected application error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static string WriteStartupErrorLog(Exception exception, bool startup = true)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppEdition.RuntimeDirectoryName);
        var logPath = Path.Combine(directory, startup ? "startup-error.log" : "runtime-error.log");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                logPath,
                $"TimestampUtc: {DateTime.UtcNow:O}{Environment.NewLine}" +
                $"Version: {AppEdition.Version}{Environment.NewLine}" +
                $"OS: {Environment.OSVersion}{Environment.NewLine}" +
                $"ProcessArchitecture: {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}{Environment.NewLine}" +
                $"Framework: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}{Environment.NewLine}" +
                $"BaseDirectory: {AppContext.BaseDirectory}{Environment.NewLine}{Environment.NewLine}" +
                exception);
        }
        catch
        {
            // Startup diagnostics must never hide the original exception.
        }

        return logPath;
    }
}
