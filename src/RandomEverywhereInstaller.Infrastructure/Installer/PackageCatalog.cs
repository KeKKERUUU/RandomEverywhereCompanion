using RandomEverywhereInstaller.Core.Installer;
using RandomEverywhereInstaller.Core.Models;

namespace RandomEverywhereInstaller.Infrastructure.Installer;

internal static class PackageCatalog
{
    private const string MainModPage = "https://flightsim.to/addon/44159/hpg-airbus-h145-action-pack-hems-random-and-anywhere-mission";
    private static readonly IReadOnlySet<SimulatorType> Both = new HashSet<SimulatorType> { SimulatorType.Msfs2020, SimulatorType.Msfs2024 };
    private static readonly IReadOnlySet<SimulatorType> Only2024 = new HashSet<SimulatorType> { SimulatorType.Msfs2024 };

    public static IReadOnlyList<PackageDefinition> All { get; } =
    [
        new("FR_VEHICLES", "French Emergency Vehicles", true, 10, @"^(?=.*pack)(?=.*vehic(?:le|ule)s)(?=.*(?:^|[\s_.-])fr(?:[\s_.-]|$)).*", Both, "https://flightsim.to/file/42450/pack-vehicules-et-personnages-franais"),
        new("30WEST_HEMS", "30West HEMS Objects", true, 20, @"^(?=.*30[\s_.-]*west)(?=.*hems)(?=.*objects).*", Both, "https://it.flightsim.to/file/69699/hems-objects"),
        new("30WEST_SMOKE", "30West Smoke", false, 21, @"^(?=.*30[\s_.-]*west)(?=.*smoke).*", Only2024, MainModPage),
        new("DE_VEHICLES", "DE Vehicles", false, 30, @"^(?=.*(?:^|[\s_.-])de(?:[\s_.-]|$))(?=.*vehicles)(?=.*action.*pack).*", Both, "https://flightsim.to/file/42133/h145-action-pack-vehicles-de"),
        new("IT_VEHICLES", "IT Vehicles", false, 40, @"^(?=.*(?:^|[\s_.-])it(?:[\s_.-]|$))(?=.*vehicles)(?=.*action.*pack).*", Both, "https://flightsim.to/file/47085/h145-action-pack-vehicles-it"),
        new("US_VEHICLES", "USA Vehicles", false, 50, @"^(?=.*(?:^|[\s_.-])us(?:a)?(?:[\s_.-]|$))(?=.*vehicles)(?=.*action.*pack).*", Both, "https://flightsim.to/file/41995/h145-action-pack-vehicles-us"),
        new("NL_VEHICLES", "NL Vehicles", false, 60, @"^(?=.*(?:^|[\s_.-])nl(?:[\s_.-]|$))(?=.*vehicles)(?=.*action.*pack).*", Both, "https://flightsim.to/file/45600/h145-action-pack-vehicles-nl"),
        new("UK_VEHICLES", "UK Vehicles", false, 70, @"^(?=.*(?:^|[\s_.-])uk(?:[\s_.-]|$))(?=.*vehicles)(?=.*action.*pack).*", Both, "https://flightsim.to/file/42197/h145-action-pack-vehicles-uk"),
        new("ANDREW_MISSION_PACK", "Andrew Mission Pack", true, 90, @"^(?=.*h145)(?=.*andrew)(?=.*mission)(?=.*pack).*", Both, MainModPage),
        new("BUNDLE_VOICE_PACK", "Bundle Voice Pack", false, 100, @"^(?=.*bundle)(?=.*voice)(?=.*pack).*", Both, "https://it.flightsim.to/file/80628/hpg-airbus-h145-action-pack-hems-random-and-anywhere-realistic-voice-pack"),
        new("DUSTIN_VOICE_PACK", "Dustin Voice Pack", false, 110, @"^(?=.*h145)(?=.*dustin)(?=.*voice)(?=.*pack).*", Both, "https://it.flightsim.to/file/81564/hems-random-and-anywhere-realistic-german-voice-pack-add-on-hpg-h145"),
        new("H145_HEMS_OBJECTS", "68ponyGT HEMS Objects", true, 999, @"^(?:(?=.*h145)(?=.*action.*pack)(?=.*hems)(?=.*objects)|(?=.*68ponygt)(?=.*hems)(?=.*objects)).*", Both, "https://flightsim.to/file/47913/h145-action-pack-hems-objects")
    ];
}
