# Random & Everywhere Companion

A Windows companion for installing and updating Random & Everywhere and running either recorded soundpacks or synthesized mission voices.

This is an independent, unofficial community project. It is not affiliated with
or endorsed by HPG, Microsoft, Asobo Studio, Flightsim.to, or third-party package
authors. Aircraft, simulator files, recorded soundpacks and downloaded voice models
are not supplied by this repository.

The `production` branch is the public source snapshot. Active development is kept
separately; this repository contains the application, required assets, build tools,
tests and attribution notices, not private development discussions or user data.
A production branch name does not turn a preview build into a stable release.

## Requirements

- Windows 10/11, x64.
- Microsoft Flight Simulator 2020 or 2024, with the H145 packages required by your
  chosen mission workflow installed separately and used under their own terms.
- .NET Framework 4.8 for the compatibility voice worker. The primary published
  EXE includes its .NET 10 desktop runtime; end users do not need the .NET SDK.
- Write permission for selected package/storage locations and sufficient space
  for downloads, staging and targeted backups. Do not run as administrator by default.
- Internet access only for online update checks, requested downloads and external
  author pages. Local editing and installed recorded packs do not require downloads.

## Download and first use

Use the executable attached to a [published release](https://github.com/KeKKERUUU/RandomEverywhereCompanion/releases)
when one is available, or build from source below. GitHub's source ZIP is not an
installer. Keep the executable name `RandomAndEverywhereCompanion.exe` for updates.

1. Keep an independent backup and close MSFS before installing packages or editing
   offline aircraft databases.
2. Read the first-run information, choose a language and verify the detected or
   selected Community folder. English is the reference language; German, French,
   Spanish and Italian are generated translations that can be changed at runtime.
3. Review the installed package inventory before adding files. Drop supported
   archives from any folders; the original archives are not modified.
4. Review the proposed installation/update and confirm it. Optional packages can
   be skipped; knowingly proceeding without dependencies may limit mission content.
5. On **Sound**, choose either recorded or synthetic voices, configure the pack
   and output, then explicitly connect to the simulator. Disconnect before changing
   session configuration. Recorded previews are explicit user actions.

The application itself does not require a terminal or PowerShell. The development
and diagnostic scripts below are optional tools for contributors and support.

The startup information is shown only until the user first continues; this local preference contains no acceptance timestamp or telemetry. On later launches, the companion checks the saved Community folder or detects it from the simulator configuration. Missing required packages open the Mission inventory, where installation can be started explicitly. Otherwise the last main page is restored. Folder selection is shown only when automatic detection is unavailable or ambiguous.

The compact main screen has **Mission** and **Sound** pages, with simulator connection controls always available on both. Mission shows the real package inventory and maintenance actions; Sound keeps recorded and synthetic selection separate. Voice, maintenance and application settings open inline, with a return action to the originating page. Shared controls use one theme and consistent action sizes; recorded XML registration and sound preview are available directly from Sound.

## One application, two separate audio modes

- **Recorded soundpacks:** discovers `SoundList` XML by content in the selected Community, including nested folders and custom filenames. Supports multiple variants, per-message mute, output device and volume. It streams the original audio without re-encoding, radio filtering or synthesized fallback.
- **Synthetic voices:** adapts the HPG Mission Companion 0.28 engine and EN16 text pack, including Windows OneCore/Piper, 12 actors, mission/dispatcher context, synthetic-only crew stand-ins, per-shift voice draws, livery/custom call signs, radio effects, headset/external outputs and settings profiles. Checklist remains actor 11; actor 12 is the helicopter rescuer. The recommended profile downloads missing models only after confirmation.
- Only one mode runs at a time. Switching modes stops the previous session, queue, playback and simulator subscription. Recorded-file messages are rejected by the synthetic voice server; sound effects declared by synthetic text packs remain supported.
- Recorded playback retains the standalone manager's shared 44.1 kHz stereo float mixer, streaming and WDL sample-rate conversion when required. Its live master volume is independent of synthetic voice effects. Mono clips are duplicated to stereo; unsupported multichannel clips are rejected rather than silently remixed.
- Original recorded mappings use master volume only. An optional `Channel="crew"` or `Channel="checklist"` on `SoundList` or individual `Sound` elements opts into the corresponding mission volume; `Channel="independent"` uses master only. Individual values override the pack default. Preview playback bypasses mission attenuation. Existing files are never rewritten to add this metadata.
- Recorded master mute retains the selected volume, including changes made while muted; it never changes synthetic volume. The main panel reports the last recording started, not an inferred playing/stopped indicator.
- In recorded audio options, **Add XML** or drop one or more `SoundList` XML files to register external packs. Audio stays in its original location; references are confined to the XML folder tree or the same package's aircraft Sound folders and must not follow links. **Reload** rechecks files. **Unregister** hides only the library entry; it does not delete files. Re-add its XML to restore it. Synthetic text-pack import remains a separate control.
- Mapping diagnostics retain missing-file rows, available-variant counts and explicit channels. Select a playable row to preview it; the general test prioritizes `packtest` in the variable or filename. Repeated variants preserve the author's random-selection weighting. OGG is reported unsupported before playback; no codec or transcoding is added. Decode failures are logged, never replaced with synthetic speech.
- Audio is resolved next to the mapping XML first. Bare filenames may also resolve in `SimObjects/Airplanes/*/Sound` inside the same nearest manifest-defined package (including root-level Italian mappings). Ambiguous matches are reported instead of choosing a different recording. Other packages and linked folders are never searched.
- Recorded playback admits at most 32 simultaneous clips/decoder preparations. Excess new clips are logged and skipped; active clips are not cut off. This does not normalize or limit the waveform: overlapping loud recordings may still clip at output, as in the standalone mixer. Idle previews release the output; active sessions retain one continuous output.
- Diagnostics exposes the bounded runtime log with a copy action. Recorded registrations and hidden entries are saved in `runtime-preferences.json` in audio storage. Periodic status polling does not rebuild or transfer the full sound library.
- The application shares Community selection, package inventory, installation, layout/index maintenance, localization, diagnostics and the GitHub application updater.

Mission index maintenance is now built into C#: it does not execute or require `Update Mission Index.cmd`, CMD, PowerShell or a downloaded generator. It reads JSON only beneath immediate Community packages' `hpgmission` folders, merges equal relative folders and writes Base64 JSON entries under `*`, matching the HPG index hierarchy. JSON is normalized with ASCII-safe Unicode escapes for legacy `atob` consumers; accents and non-Latin text are preserved rather than replaced with `?`. The existing index is replaced only after all inputs validate. Cancellation/failure preserves the previous index and the installation transaction's rollback. Linked paths are rejected rather than followed outside Community. Bounds: 256 MiB per mission, 1 GiB generated index, 10,000 files and 64 nested mission directories. Live simulator compatibility still needs VM acceptance; fixture tests cover the file-format contract and safety behavior.

H145 build 500 maintenance is an explicit, separate action, also available when R&E prerequisites are missing. Build 500 or later skips every patch download and rebuilds only the installed aircraft package layouts. Earlier detected builds update the Base Pack and, only when already installed, the Action Pack. Unknown versions are blocked. The confirmation shows the selected path, operation and configured public sources; no aircraft patch is bundled.

Disk space is checked before downloading and again against expanded patch sizes and targeted rollback files. H145 staging stays on the Community drive, without a fallback to the system drive. Only the expected aircraft roots can be merged. Layouts are rebuilt **after** patch application; cancellation/failure restores touched files, layout absence and manifest metadata. Success cleans the download/staging/rollback session; incomplete recovery retains it, and locked cleanup files are logged for later removal. The H145 action does not execute the mission index script.

Synthetic notification effects may be copied unchanged from the installed R&E mission into voice storage if absent; existing user effects are preserved. Recorded voice mappings are never used as synthetic fallback. No notification sound assets are bundled or downloaded during playback. Custom random-voice pools round-trip through profiles; the current UI exposes the random toggle, not a pool editor.

## Packages and maintenance

Select the simulator and Community before adding archives. The existing inventory remains distinct from dropped files. The installer supports nested package/container roots, the required/optional installation order, stable package replacement and cumulative `everywhere_all.json` version comparison.

| Package | Requirement |
| --- | --- |
| French Emergency Vehicles | Required; nested container folders are normalized |
| 30West HEMS Objects | Required |
| 30West Smoke | Optional, MSFS 2024 only, after 30West HEMS |
| DE / IT / USA / NL / UK Vehicles | Optional, in this order |
| Andrew Mission Pack | Required primary mission package |
| Bundle Voice Pack / Dustin Voice Pack | Optional |
| 68ponyGT HEMS Objects | Required, applied last |

Offshore Objects is not managed or installed. Removing it from the catalog does
not delete an existing installation. Archive handling supports ZIP, RAR, 7z and
the other container formats listed in `ArchivePackageInspector`; encrypted,
corrupt, unsafe or unsupported archive variants are not guaranteed to work.

Stable mission updates replace the selected primary package transactionally.
Cumulative updates accept a standalone `everywhere_all.json`, require the primary
package and compare mission/development versions before replacement. Installed
inventory and merely selected archives are separate states.

Installation uses targeted rollback backups. Successful installation or rollback removes its temporary session; failed recovery retains the recovery data. Original archives and recordings are not edited. Layouts are regenerated for supported R&E packages and the packages being installed, not arbitrary Community folders.

The shared `68ponygt-actionpack-std-objects` package uses the versioned author-supplied fingerprint in `fingerprints/`. Existing canonical, unknown or unlisted files are preserved. Known older files are replaced only by verified canonical content; missing files can be added. Only files actually changed are backed up. Unknown future releases require refreshed fingerprints, not a manifest-version guess. Layout regeneration remains a separate scoped operation after the merge.

Debug cleanup requires confirmation and a verified ZIP backup. It only removes recognized debug/OSM/test snapshots from a selected mission `work` folder; global settings, saved games and unrelated user files are preserved. Debug backups and small settings-history backups are deliberately retained.

## World database and custom waypoints

Open **Mission → Tools → World database & waypoints**. This integrates the
collaborator's World Database Update feature from upstream commit `53735bd`,
without importing its WinForms UI or its automatic scheduler.

- Create/edit/remove local hangar and hospital waypoints, airports (primary,
  secondary, military), aprons, hospital helipads, helipads and runways.
- Paste decimal/DMS/aviation coordinates or a supported map link. ASCII name
  conversion requires confirmation; unsupported characters are identified.
- Save affects only your local list. **Write waypoints** explicitly merges local
  entries with the last verified published data into the selected H145 `work`
  folder. Local waypoint IDs override published IDs. The revision is published
  as `L:HMC_DATA_REV` on the next simulator connection in either audio mode.
- **Download shared data** uses Andrew's mission repository and checks its
  allowlisted manifest paths, sizes and SHA-256 values. A failed download may use
  the last verified cache, clearly reported; there is no automatic download.
- **Apply databases** previews the affected files and record counts, requires
  confirmation and a closed simulator, and touches only databases inside the
  selected Community's H145 Base/AP packages. Originals are retained, external
  changes invalidate previews, and failure/cancellation rolls back touched files.
  Package layouts are regenerated after application or restoration.
- **Share via GitHub** copies the contribution JSON and opens a new issue in
  Andrew's mission repository. You review and submit it; the app has no GitHub
  write token and does not upload or commit data automatically.

World data is kept under `WorldData` in selected companion storage, independently
of both audio pipelines. Local edits retain a `.bak`; original database snapshots
remain available until restored. An incomplete transaction retains its recovery
snapshot and blocks further database changes pending review. Live tablet refresh
and in-simulator waypoint behavior still require VM acceptance.

## Storage and runtime

- Small shared preferences: `%LocalAppData%\RandomAndEverywhereCompanion\settings.json`.
- Default voice/model storage: a companion-owned folder beside Community, on the same drive where possible.
- Selecting another storage parent creates its `RandomEverywhereCompanionAudio` subfolder. Existing models in the old location are preserved, not automatically deleted or migrated.
- Piper installation stages its archive on the selected storage drive, validates paths/sizes, then replaces only its managed engine folder. Temporary staging and partial downloads are cleaned up.
- Installer logs: `%LocalAppData%\RandomAndEverywhereInstaller\Logs`. Voice diagnostics: `voice-runtime.log` in voice storage.

The WPF shell uses .NET 10. The compatibility voice worker uses .NET Framework 4.8 for the existing OneCore integration; its small binaries are embedded in the single distribution EXE and extracted lazily when an audio page is opened. Windows 10/11 with .NET Framework 4.8 are required. The worker runs without a visible console and exits with its parent.

Piper and voice models are not bundled. The unmodified native x64 `SimConnect.dll` **is bundled**, as explicitly authorized by the project owner. It is embedded in the single EXE and extracted with the worker; no SDK installation or manual DLL selection is needed for normal use. Settings offers an advanced SDK override. See `THIRD-PARTY-NOTICES.md` and the native component notice for provenance and hash. Live audio/SimConnect behavior, especially MSFS 2020 LVAR support, must still be verified in a simulator VM.

## Build and tests

Install Git and the .NET SDK selected by `global.json` (10.0.301, with compatible
patch roll-forward) on Windows. Restore requires access to NuGet; no credentials
belong in the repository. Clone the public production branch:

```powershell
git clone --branch production https://github.com/KeKKERUUU/RandomEverywhereCompanion.git
cd RandomEverywhereCompanion
dotnet restore RandomEverywhereInstaller.slnx
dotnet build RandomEverywhereInstaller.slnx -c Release -m:1
```

Run the complete isolated suite:

```powershell
.\tools\Test-All.ps1 -Configuration Release
```

The runner builds the solution and explicitly executes the custom test harnesses.
`dotnet test` alone does not discover these checks; a build target rejects that
command rather than returning a misleading success. The runner fails on a failed build, nonzero test exit or missing
completion report. Use `-Configuration Debug` to verify the Debug artifacts;
`-DotNetPath` selects an SDK executable and `-TemporaryRoot` allows a short local
fixture path for the .NET Framework suite.

The Windows verification workflow runs the same harnesses in Debug and Release,
with read-only repository permissions and no production app or audio startup.
After a successful build the runner attempts all three independent suites and
reports every failed suite before returning a failure; an earlier failed suite
cannot hide later runtime or WPF regressions. Linux cross-builds can check
compilation, but Windows-specific path, junction, process and WPF tests require
the Windows gate. Passing a selected portable subset is not a complete run.

The October 2026 audit adds regressions for missing-input scan recovery,
automatic text-pack selection, non-finite simulator counters, Windows archive
path aliases, native-index errors/cancellation without launching a helper, and
failures in completion reporting or diagnostic logging.
Post-commit reporting/cleanup failures cannot enter the rollback path; a failed
diagnostic write cannot prevent pre-commit rollback. This remains exception-safe
rollback, not a durable crash-recovery journal.

Tests use synthetic archives and temporary directories; they do not show application windows, play audio or connect to the simulator. They cover package recognition, rollback, scoped writes, JSON updates, localization, embedded payload completeness, recorded mappings and profile compatibility. Windows junction and isolated child-process fixtures exercise cleanup and cancellation; no real Community folders or maintenance scripts are used.

Recorded-audio checks additionally compare decoded PCM samples at unity gain,
overlapping inputs, live master/channel levels, WDL mono conversion, source-file
hashes and stream disposal using a fake output device. These checks do not prove
perceptual parity or device behavior; compare the same recordings with the
standalone RE Audio Manager on the same output device and level in the user's VM.

## Single-file distribution

```powershell
dotnet publish .\src\RandomEverywhereInstaller.App -c Release -p:PublishProfile=SlimWinX64 -m:1
```

The primary asset is `RandomAndEverywhereCompanion.exe`, a compressed, self-contained x64 EXE. Optional voice models remain external. No MAUI, Blazor, WebView or browser runtime is included.

The in-app updater reads `KeKKERUUU/RandomEverywhereCompanion/releases/latest` and
expects this exact asset name. It verifies the release SHA-256 digest and size,
then applies replacement after the running process exits. It uses public releases
without embedded credentials. Only stable releases are discovered by this endpoint;
preview releases must currently be downloaded manually. Builds targeting the older
private repository need a one-time manual replacement to migrate update sources.
The public source repository may exist before its first binary release.

Despite its historical name, `SlimWinX64` is a compressed, self-contained single
EXE profile, not framework-dependent. `OfflineWinX64` is an uncompressed,
ReadyToRun self-contained single EXE. `FastFolderWinX64` is a developer-oriented
folder build, not the primary distribution. WPF trimming is disabled. Do not commit
published executables, model files or user packages; attach binaries to releases.

## Source layout

| Location | Responsibility |
| --- | --- |
| `src/RandomEverywhereInstaller.App` | WPF shell, MVVM, inline workflows, five-language UI |
| `src/RandomEverywhereInstaller.Core` | Domain contracts, wizard and version policies |
| `src/RandomEverywhereInstaller.Infrastructure` | Archives, scoped transactions, layouts/index, updates and world data |
| `src/RandomEverywhereInstaller.CompanionRuntime` | .NET Framework voice worker, separate recorded/synthetic paths, local IPC |
| `tests/RandomEverywhereInstaller.Tests` | Core/installer, file-format and safety fixtures |
| `tests/CompanionRuntime.Tests` | Audio/protocol tests using fake output devices |
| `tests/RandomEverywhereInstaller.App.Tests` | Headless WPF commands, binding and layout checks |
| `tests/RandomEverywhereInstaller.UiHost` | Synthetic UI review host, not a production entry point |
| `fingerprints` | Versioned shared-object protection data |
| `tools` | Complete test runner and optional archive diagnostics |

## Troubleshooting and contributing

- Missing package: verify the selected simulator/Community and inspect archive
  diagnostics. An archive being available does not mean it is already installed.
- Missing or ambiguous recording: inspect **Sound → audio options** and diagnostics.
  Keep the mapping XML with its original audio; do not rename files blindly.
- Failed update: inspect the installation log and
  `%LocalAppData%\RandomAndEverywhereInstaller\Updates\update-error.log`. A preview
  will not appear through the stable-release check.
- Recovery files retained: stop further changes and review the reported recovery
  location. Do not delete backups just to dismiss an error.
- Antimalware warning: preserve the detection name and executable SHA-256 for
  investigation. Do not disable protection or assume a false positive.

Report reproducible defects through this repository's Issues. Include version,
Windows/simulator version, steps, expected/actual behavior and a sanitized log.
Do not upload proprietary packages, personal files, credentials or private paths.
Run both Debug and Release suites for code changes. Use synthetic fixtures for
destructive paths, and keep changes scoped to the selected workflow.

## Verification limits

The integrated baseline passes 280 isolated checks in both Debug and Release.
CI runs the same harnesses on Windows. These results do not prove live simulator,
EFS storage, Bluetooth/device, perceptual audio or long-session behavior. Native
mission-index compatibility, world-data tablet refresh and output-device parity
still require simulator acceptance. No backup system guarantees recovery from
hardware failure or interrupted power.

## Attribution and permissions

The voice runtime is adapted from [HPG Mission Companion](https://github.com/davierosoft/HPG-Mission-Companion), commit `282a5da` (0.28), with collaborator consent confirmed by the project owner. It retains the local IPC, path validation, mode isolation and lifecycle adaptations; it is not the upstream application binary. Recorded-pack behavior is adapted from [R&E Audio Manager](https://github.com/KeKKERUUU/RE_AUDIO_MANADER_V2), commit `e146625`.

The audit incorporates the automatic text-pack fallback order reviewed against
upstream commit `5dd11c9` (0.37): saved usable choice, mission-declared pack,
configured language, English, then another usable pack. The shell displays the
worker's effective choice. The embedded engine and content remain 0.28 / EN16;
this selected fix does not establish full 0.37 compatibility. Mission 169.66
requires EN21 and adds speech keys absent from EN16. Importing EN21 alone also
leaves its priority, expiry and stand-in attributes unsupported. Track the
engine/content integration and live acceptance separately. Do not interpret a
successful package installation as support for every newer speech protocol.

The upstream HPG Mission Companion license is preserved in `src/RandomEverywhereInstaller.CompanionRuntime/UPSTREAM-LICENSE.txt`. It is consent-based, not a permissive MIT license; access to this integration does not grant third parties unrestricted reuse of that code. See [third-party notices](THIRD-PARTY-NOTICES.md). No simulator, commercial aircraft or downloaded voice-model files are redistributed.
