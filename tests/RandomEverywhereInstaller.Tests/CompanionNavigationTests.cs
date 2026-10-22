using System.Xml.Linq;
using RandomEverywhereInstaller.Core.Companion;

namespace RandomEverywhereInstaller.Tests;

internal static partial class Program
{
    private static void CompanionUsesOneControlTheme()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "RandomEverywhereInstaller.App");
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var application = XDocument.Load(Path.Combine(app, "App.xaml"));
        var dictionaries = application.Descendants(ui + "ResourceDictionary").Attributes("Source").Select(a => a.Value).ToArray();
        Equal("Themes/Colors.xaml,Themes/Theme.xaml", string.Join(',', dictionaries));
        var theme = XDocument.Load(Path.Combine(app, "Themes", "Theme.xaml"));
        var keys = theme.Root!.Elements().Select(e => (string?)e.Attribute(x + "Key") ?? "type:" + (string?)e.Attribute("TargetType")).ToArray();
        Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        foreach (var key in new[] { "BaseButton", "PrimaryButton", "SuccessButton", "SecondaryButton", "GhostButton", "ChromeButton" })
            Equal(1, keys.Count(k => k == key));
        var wizard = XDocument.Load(Path.Combine(app, "InstallerView.xaml"));
        Equal("UserControl", wizard.Root!.Name.LocalName);
        False(wizard.Descendants(ui + "Viewbox").Any(), "The installer must not shrink the entire UI and its text.");
        False(wizard.Descendants(ui + "ControlTemplate").Any(),
            "Installer chrome must reuse shared templates.");
        False(File.Exists(Path.Combine(app, "MainWindow.xaml")), "The obsolete standalone wizard must not remain as a second UI.");
    }

    private static void CompanionDetailsStayInShell()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "RandomEverywhereInstaller.App");
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var details = XDocument.Load(Path.Combine(app, "CompanionOptionsView.xaml"));
        Equal("UserControl", details.Root!.Name.LocalName);
        False(File.Exists(Path.Combine(app, "CompanionOptionsWindow.xaml")), "No secondary settings window should remain.");
        var shell = XDocument.Load(Path.Combine(app, "CompanionWindow.xaml"));
        True(shell.Descendants().Any(e => (string?)e.Attribute(x + "Name") == "InlineOptions"), "Contextual details must be embedded.");
        var commands = shell.Descendants(ui + "Button").Attributes("Command").Select(a => a.Value).ToArray();
        False(commands.Contains("{Binding ToggleAdvancedCommand}"), "The redundant Advanced menu must be removed.");
        True(commands.Contains("{Binding OpenAudioOptionsCommand}"), "Audio toolbar must be reachable in one action from the main page.");
        var detailCommands = details.Descendants(ui + "Button").Attributes("Command").Select(attribute => attribute.Value).ToArray();
        foreach (var command in new[] { "RegisterRecordedCommand", "ReloadRecordedCommand", "ForgetRecordedCommand" })
        {
            var binding = "{Binding " + command + "}";
            Equal(1, detailCommands.Count(value => value == binding));
            False(commands.Contains(binding), "Approved single recorded toolbar was duplicated in the shell.");
        }
        False(details.Descendants(ui + "ProgressBar").Any(), "Details must not duplicate shell progress.");
        False(details.Descendants(ui + "Button").Any(e => (string?)e.Attribute("Command") == "{Binding SaveActorsCommand}"),
            "Actor and audio settings must share one save action.");
    }
}
