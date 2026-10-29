# Building and testing

[Documentation index](README.md) · [App overview](../README.md)

These instructions apply to the public version 1.2.5 source. Building an executable
does not create a GitHub release or an installer.

## Prerequisites

Use Windows, Git and the .NET SDK specified in [global.json](../global.json)
(10.0.301, with compatible patch roll-forward). Restore requires access to NuGet.
The .NET Framework 4.8 voice worker is built alongside the WPF application.

## Build

```powershell
git clone --branch production https://github.com/KeKKERUUU/RandomEverywhereCompanion.git
cd RandomEverywhereCompanion
dotnet restore RandomEverywhereInstaller.slnx
dotnet build RandomEverywhereInstaller.slnx -c Release -m:1
```

## Run the tests

```powershell
.\tools\Test-All.ps1 -Configuration Debug
.\tools\Test-All.ps1 -Configuration Release
```

These projects use executable test harnesses, not standard test adapters.
`dotnet test` is not a substitute for the runner. The runner rejects failed builds,
nonzero test exits and missing completion reports, and attempts all three suites
after a successful build.

Optional runner parameters:

- `-DotNetPath`: path to a particular SDK executable.
- `-TemporaryRoot`: short local fixture directory for Windows/.NET Framework tests.

The suites cover installer transactions, path validation, package recognition,
mission indexing, world data, audio/protocol behavior and WPF bindings/layouts.
Audio tests use fake output devices and synthetic inputs. They do not establish
real-device, listening or simulator compatibility. Windows-specific junction,
process and WPF tests must run on Windows.

## Publish an executable

```powershell
dotnet publish .\src\RandomEverywhereInstaller.App -c Release -p:PublishProfile=SlimWinX64 -m:1
```

The output is `RandomAndEverywhereCompanion.exe`, a compressed, self-contained
x64 executable. Piper models remain external. Keep the executable name unchanged
when using the application updater.

Available profiles:

| Profile | Output |
| --- | --- |
| SlimWinX64 | Compressed, self-contained single executable |
| OfflineWinX64 | Uncompressed, ReadyToRun self-contained single executable |
| FastFolderWinX64 | Self-contained folder build |

WPF trimming is disabled. These profiles do not build a setup installer.

## Source layout

| Location | Responsibility |
| --- | --- |
| src/RandomEverywhereInstaller.App | WPF shell, view models and localization |
| src/RandomEverywhereInstaller.Core | Domain contracts and policies |
| src/RandomEverywhereInstaller.Infrastructure | Archives, transactions, indexing, updates and world data |
| src/RandomEverywhereInstaller.CompanionRuntime | .NET Framework voice worker |
| tests/RandomEverywhereInstaller.Tests | Installer and infrastructure harness |
| tests/CompanionRuntime.Tests | Runtime and audio harness |
| tests/RandomEverywhereInstaller.App.Tests | Headless WPF harness |
| tests/RandomEverywhereInstaller.UiHost | Isolated UI review host |
| fingerprints | Shared-package reference fingerprints |
| tools | Test runner and diagnostic tools |

For contributions, run both configurations and use isolated fixtures for file
changes. Do not commit generated executables, downloaded models, user settings or
commercial packages. Preserve attribution and third-party notices.
