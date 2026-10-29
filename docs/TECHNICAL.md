# Technical reference

[Documentation index](README.md) · [App overview](../README.md)

This reference describes public source version 1.2.5. It is intended for advanced
configuration, troubleshooting and contributors; it does not describe unpublished
features or guarantee compatibility with newer missions.

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

Mission index maintenance is now built into C#: it does not execute or require `Update Mission Index.cmd`, CMD, PowerShell or a downloaded generator. It reads JSON only beneath immediate Community packages' `hpgmission` folders, merges equal relative folders and writes Base64 JSON entries under `*`, matching the HPG index hierarchy. JSON is normalized with ASCII-safe Unicode escapes for legacy `atob` consumers; accents and non-Latin text are preserved rather than replaced with `?`. The existing index is replaced only after all inputs validate. Cancellation/failure preserves the previous index and the installation transaction's rollback. Linked paths are rejected rather than followed outside Community. Bounds: 256 MiB per mission, 1 GiB generated index, 10,000 files and 64 nested mission directories. Real simulator compatibility must be checked separately from file-format tests.

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
and in-simulator waypoint behavior still require testing in the simulator.

## Storage and runtime

These paths describe the current public source, not a future installer layout.

- Shared preferences: `%LocalAppData%\RandomAndEverywhereCompanion\settings.json`.
- When Community is first selected and no audio path is saved, the application sets
  `<Community parent>\.RandomEverywhereCompanion\Audio`. Before that selection,
  its fallback is `%LocalAppData%\RandomAndEverywhereCompanion\Audio`.
- Choosing another storage parent creates a `RandomEverywhereCompanionAudio`
  subfolder. Existing models at the old location are not automatically migrated or deleted.
- Installer logs: `%LocalAppData%\RandomAndEverywhereInstaller\Logs`.
  Voice diagnostics: `voice-runtime.log` in audio storage.
- Mission-download cache: `<Community parent>\.RandomEverywhereCompanion\MissionDownloads`.
  Installation sessions contain staging and targeted rollback backups.

The shell uses .NET 10; the voice worker uses .NET Framework 4.8. The single-file
publish profile embeds the worker binaries and extracts them when needed. The
worker runs without a console and exits with its parent.

The native x64 SimConnect DLL is included. The optional manual SDK override is for
troubleshooting, not normal setup. Provenance and licensing are in
[third-party notices](../THIRD-PARTY-NOTICES.md) and the
[native component notice](../src/RandomEverywhereInstaller.CompanionRuntime/Native/NOTICE.md).

## Application updates

The updater checks
`KeKKERUUU/RandomEverywhereCompanion/releases/latest` for
`RandomAndEverywhereCompanion.exe`, verifies its release digest and size, and
replaces the executable after the running process exits. The endpoint returns
stable releases, not prereleases. No public binary release is currently available.

## Compatibility and recovery limits

The embedded synthetic engine is 0.28 with EN16. Its selected text-pack fallback
policy does not implement the newer engine's scheduling, expiry and message-level
stand-in contracts. Newer mission text packs can contain unsupported messages or
attributes even when their XML parses successfully.

File-operation rollback protects against handled errors and cancellation; it is
not a durable crash-recovery journal. Preserve retained recovery data. Hardware
failure or interrupted power requires separate backup/recovery planning.

Linked-path restrictions in this source can prevent indexing or maintenance for
some Community layouts. Do not disable path protections to work around a failure;
report the affected layout with personal paths removed.

Live MSFS compatibility, EFS storage, Bluetooth devices, long sessions and
perceptual audio quality require separate testing. Passing fixture tests alone
does not establish those outcomes.
