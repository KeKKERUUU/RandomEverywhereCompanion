using System.Reflection;
using System.Text.Json;
using RandomEverywhereInstaller.App.ViewModels;

namespace RandomEverywhereInstaller.App.Tests;

internal static partial class Program
{
    private static void TextPackFallbackSelection()
    {
        using var fixture = new Fixture();
        var packs = new[] { Pack("DE", 9), Pack("EN", 16), Pack("FR", 1) };
        ApplyPackSnapshot(fixture.Vm, packs, selected: "", language: "EN");
        Check(fixture.Vm.TextPack?.Id == "EN", "An English user received alphabetical DE");
        ApplyPackSnapshot(fixture.Vm, packs, selected: "missing", language: "FR");
        Check(fixture.Vm.TextPack?.Id == "FR", "Missing saved selection ignored the configured language");
        ApplyPackSnapshot(fixture.Vm, packs, selected: "missing", language: "IT");
        Check(fixture.Vm.TextPack?.Id == "EN", "An unavailable language did not fall back to English");
        ApplyPackSnapshot(fixture.Vm, packs, selected: "fr", language: "EN");
        Check(fixture.Vm.TextPack?.Id == "FR", "An explicit usable selection was not matched case-insensitively");
        ApplyPackSnapshot(fixture.Vm, [Pack("DE", 9)], selected: "", language: "EN");
        Check(fixture.Vm.TextPack?.Id == "DE", "Last-resort usable selection was lost");
    }

    private static void TextPackRuntimeSelection()
    {
        using var fixture = new Fixture();
        ApplyPackSnapshot(fixture.Vm, [Pack("Broken", 0, false), Pack("DE", 9), Pack("EN", 16), Pack("FR", 1)],
            selected: "", language: "EN", effective: "FR");
        Check(fixture.Vm.TextPack?.Id == "FR", "The shell replaced the worker's mission-aware selection");
        Check(fixture.Vm.TextPacks.All(pack => pack.Id != "Broken"), "An unusable pack remained selectable");
        ApplyPackSnapshot(fixture.Vm, [Pack("Broken", 0, false)], selected: "Broken", language: "EN", effective: "");
        Check(fixture.Vm.TextPack is null && fixture.Vm.TextPacks.Count == 0, "An unusable-only library invented a usable selection");
        ApplyPackSnapshot(fixture.Vm, [new { Name = "Broken", Version = 0, Check = "Legacy fixture" }, new { Name = "EN", Version = 16, Check = "Legacy fixture" }],
            selected: "Broken", language: "EN");
        Check(fixture.Vm.TextPack?.Id == "EN" && fixture.Vm.TextPacks.Count == 1, "A legacy snapshot without Usable exposed an invalid pack");
    }

    private static object Pack(string name, int version, bool usable = true) => new { Name = name, Version = version, Usable = usable, Check = "Fixture" };

    private static void ApplyPackSnapshot(CompanionViewModel vm, object[] packs, string selected, string language, string? effective = null)
    {
        var snapshot = new Dictionary<string, object?>
        {
            ["Running"] = false, ["SimConnected"] = false, ["SimStatus"] = "Fixture", ["Logs"] = Array.Empty<string>(),
            ["Devices"] = new[] { "" }, ["Voices"] = Array.Empty<object>(), ["RecordedPacks"] = Array.Empty<object>(),
            ["TextPacks"] = packs, ["Actors"] = Array.Empty<string>(),
            ["Preferences"] = new { RecordedPack = "", RecordedDevice = "", RecordedVolume = 100, RecordedMasterMuted = false },
            ["Settings"] = new
            {
                BridgePack = selected, DefaultLanguage = language, MasterVolume = 100,
                AudioOut = new { HeadsetDevice = "", ExternalDevice = "", HeadsetLevel = 100, ExternalLevel = 100, NotifyLevel = 100 },
                Radio = new { Quality = "clean", Beep = "none", NoiseLevel = 0, BeepLevel = 0, Tail = false },
                AutoAudioInhibit = true, UseMissionVolumes = true, Variety = "normal", CallSignMode = "livery", CallSign = "",
                Languages = new Dictionary<string, object>()
            }
        };
        if (effective is not null) snapshot["EffectiveTextPack"] = effective;
        typeof(CompanionViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [JsonSerializer.SerializeToElement(snapshot), true]);
    }
}
