# Random & Everywhere Companion

A Windows companion for installing and updating Random & Everywhere mission packages and managing mission audio in Microsoft Flight Simulator.

## Download

Download Random & Everywhere Companion from the [Releases page](https://github.com/KeKKERUUU/RandomEverywhereCompanion/releases), where you’ll also find installation instructions and release notes.

To build the application yourself, see [Building from source](docs/BUILDING.md).

## Features

- Check installed mission packages and manage supported package installations and updates.
- Play recorded soundpacks with pack selection, audio previews, mute and volume controls.
- Use Windows or Piper voices for synthetic mission speech, with crew voice assignments and radio effects.
- Manage local waypoints and shared world data.
- Access maintenance tools and diagnostic logs.

Recorded soundpacks and synthetic voices remain separate. Aircraft, mission packages, recorded soundpacks and Piper voice models are obtained separately.

## Requirements

- Windows 10 or 11, 64-bit.
- Microsoft Flight Simulator 2020 or 2024.
- The H145 aircraft and additional packages required by your mission.
- .NET Framework 4.8. The application’s main .NET runtime is included in the distributed build.
- Internet access for update checks and downloads.
- Sufficient disk space and write access to the folders used for packages, audio and backups.

SimConnect is included. You do not need to install the MSFS SDK or manually select a DLL for normal use.

## Getting started

1. Download the application and follow the installation instructions provided with the release.
2. Choose your language and verify the detected simulator and Community folder.
3. Review the package inventory and install any required dependencies.
4. Open the audio settings and choose recorded soundpacks or synthetic voices.
5. Select your pack, voices and output device, then save your changes.
6. Connect the Companion to the simulator when you are ready to use mission audio.

Close MSFS before installing or updating simulator packages or applying offline maintenance. Keep a backup of any files you have customized.

Normal application use does not require a terminal, PowerShell or development tools.

## Mission audio

### Recorded soundpacks

Use recordings provided by soundpack authors. Choose a pack, preview available sounds and adjust playback settings.

Recorded audio is not replaced with synthetic speech. If a recording is missing or cannot be played, check the audio diagnostics.

### Synthetic voices

Assign voices to crew roles and configure speech and radio effects. Windows voices use your installed voice resources; Piper requires separately downloaded voice models.

Voice models may have different download sizes and licensing terms. Review the information shown before downloading.

## Updates

Use the application’s update controls to check for available updates. Application, mission and voice content may be updated separately.

Read the release notes for compatibility requirements and known limitations. Prerelease builds are intended for testing and may require manual installation.

For storage locations, supported audio formats and detailed maintenance behavior, see the [technical reference](docs/TECHNICAL.md).

## Troubleshooting

- **A package is missing:** verify the selected simulator and Community folder, then review the package inventory.
- **There is no audio:** check the selected audio mode, pack, output device, volume and simulator connection.
- **A voice or recording is unavailable:** check that the required files are installed and review the audio diagnostics.
- **An update or maintenance operation fails:** keep the logs and any recovery files reported by the application. Do not delete backups to dismiss an error.
- **Your antivirus reports a warning:** retain the detection name and file SHA-256 for investigation. Do not disable protection or assume the warning is a false positive.

## Bug reports and suggestions

Open an [issue](https://github.com/KeKKERUUU/RandomEverywhereCompanion/issues) with:

- Your application, Windows and MSFS versions.
- The steps needed to reproduce the problem.
- What you expected and what happened instead.
- Relevant screenshots or logs, with personal information removed.

Do not attach commercial packages, credentials or personal files.

## Credits and third-party software

The synthetic voice runtime is adapted from [HPG Mission Companion](https://github.com/davierosoft/HPG-Mission-Companion). Recorded-pack support is adapted from [R&E Audio Manager](https://github.com/KeKKERUUU/RE_AUDIO_MANADER_V2).

Thanks to the mission, soundpack and voice-model authors whose work makes these experiences possible.

This is an independent, unofficial community project, not affiliated with or endorsed by HPG, Microsoft, Asobo Studio, Flightsim.to or third-party package authors.

Third-party components and downloaded content retain their own licensing terms. See [third-party notices](THIRD-PARTY-NOTICES.md) and the [upstream runtime license](src/RandomEverywhereInstaller.CompanionRuntime/UPSTREAM-LICENSE.txt).
