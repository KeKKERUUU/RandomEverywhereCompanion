# SimConnect redistributable

The Windows x64 application distributes the native `SimConnect.dll` unchanged,
under its original filename. Microsoft retains ownership and the applicable MSFS
SDK terms. This file is not covered by the project's source-code license.

Distribution was explicitly confirmed as permitted by the project owner on
2026-10-17, following confirmation with the collaborator.
This records the project owner's authorization, not a new license granted by us.

Provenance of the pinned copy: the existing independent RE Audio Manager
distribution, `Dist/System/SimConnect.dll`, from the local reference at e146625.
It is byte-identical to that distribution's `runtimes/win-x64/native/SimConnect.dll`.
No patching, wrapping, signing, or binary rewriting was performed.

- Native PE32+, AMD64 (`0x8664`), 79,360 bytes.
- SHA-256: `bd15e8a094762bbdf32320f549b174340b8f1e58b63624dcc93bc50507e7b619`.
- This copy has no version resource or Authenticode signature. No SDK-version or
  signature claim is made; compatibility with the simulator is a VM acceptance test.

Build output places the DLL next to the worker. The WPF single-file application
embeds that payload and extracts it with its existing hash verification. A selected
SDK DLL in audio storage remains an explicit troubleshooting override. Do not
replace this file during dependency updates without updating provenance and tests.
