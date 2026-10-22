# Third-party notices

## HPG Mission Companion
Copyright (c) 2026 davierosoft. Portions under `src/RandomEverywhereInstaller.CompanionRuntime/Upstream`, the EN16 text pack and Recommended profile are adapted from version 0.28, commit 282a5da. The original consent-based license is retained as `UPSTREAM-LICENSE.txt`. The project owner confirmed collaborator permission for this integration. No general MIT sublicense or additional reuse permission is implied.

Changes include a headless IPC host, separate audio modes, WPF-managed configuration, managed storage, guarded downloads, and removal of the upstream UI/autostart/update workflow. Engine compatibility is reported as 0.28; mission identity is separate state, not the protocol version. Upstream sound-effect assets are not bundled; the adapter resolves approved notification filenames from the installed mission and preserves user overrides.

World database coordinate parsing, record builders, JSON formatting and delta
semantics in `Infrastructure/WorldData/WorldDataRules.cs` are additionally adapted
from `src/WorldDb.cs` and `src/SharedData.cs`, upstream commit `53735bd`, under the
same confirmed collaborator permission and retained consent-based license.
WPF editing, scoped transactions and verified network/cache handling are provided
by this integration. The original WinForms editor and scheduler are not copied.
The automatic text-pack fallback policy was additionally adapted from the
upstream 0.36/0.37 implementation reviewed at commit
`5dd11c99f85c675bb91d1040f58ca32e4c18cb15`. The existing consent and license apply.
This selected compatibility fix does not change the embedded engine/EN version
or claim support for the newer scheduler and voice-pack attributes.

## R&E Audio Manager
Recorded-pack discovery, mapping semantics and variant behavior are adapted from KeKKERUUU/RE_AUDIO_MANADER_V2, commit e146625, under the project owner's authorization. Its MAUI/Blazor distribution, development files and audio assets are not copied.

## Build dependencies
- SharpCompress 0.49.1: archive inspection/extraction; MIT. https://github.com/adamhathcock/sharpcompress
- NAudio 2.2.1 and its component assemblies: streamed recorded-audio playback; MIT. https://github.com/naudio/NAudio
- Microsoft.NETFramework.ReferenceAssemblies 1.0.3: build-only reference assemblies, not a redistributed framework runtime.
- Microsoft .NET/.NET Framework and Windows components retain their respective terms.

## SimConnect redistributable
The application includes the unmodified native x64 `SimConnect.dll` under its original filename. The project owner explicitly confirmed redistribution as permitted on 2026-10-17. Microsoft retains ownership and the applicable MSFS SDK terms; this component is not relicensed under the project's source-code license. Provenance, size and SHA-256 are recorded in `src/RandomEverywhereInstaller.CompanionRuntime/Native/NOTICE.md`, also included in the runtime payload. The DLL is embedded in the single EXE and extracted beside the worker. Normal users do not need to install the SDK or import a DLL. A manual SDK override remains available for troubleshooting.

## Optional downloads and Windows components
Piper, eSpeak NG, voice models and their model cards are obtained at runtime only when requested; their own licenses apply. Community voice source/license information is displayed before download. Windows voices come from the user's Windows installation. No H145 patches, aircraft files or paid simulator content are included.

## MIT dependency license text

NAudio: Copyright 2020 Mark Heath.

SharpCompress: Copyright (c) 2025 Adam Hathcock.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
