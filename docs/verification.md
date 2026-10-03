# Verification evidence — 2026-10-03

## Executed on Windows x64

- `python tools/build.py runtime --acceleration cpu`
  built CrispASR v0.8.41 at `340d7085eaa53c40a46dcb73a6d3d0448a480006`, with
  the checked loopback patch. `crispasr.exe --version` reported Windows x86_64,
  MSVC 1951, Release, and the CPU backend.
- `python tools/build.py windows` built the plugin and offline package installer,
  ran its .NET suite, and produced the ZIP and SHA-256. App-local CRT and OpenMP
  DLLs are included. The host SDK is excluded from the plugin ZIP.
- `.\.deps\dotnet\dotnet.exe test windows/Tests -c Release -v quiet`
  passed **18 tests**, covering authoritative final text, multiple/empty turns,
  incomplete coverage, failed native streaming contracts, fragmented UTF-8 over
  real WebSockets, disconnect, callback draining, download corruption/cancellation,
  canonical language hints, WAV duration/chunks and Windows Job Object shutdown.
- `python -m unittest discover -s tests -v` passed the six applicable Windows
  build/package checks. The two native macOS watchdog tests are skipped here.
  Package-store installation uses an isolated verification profile; it checks
  the real upstream archive validator/index writer and preference preservation.
- `.\.deps\dotnet\dotnet.exe run --project windows/Acceptance -c Release -- .build/acceptance-assets .deps/crispasr/samples/jfk.wav`
  downloaded and checksum-verified Q4_K, then passed:
  - Inspection of the child's **three loopback-only** TCP listeners by owning PID.
  - Two successive four-second real-speech recordings with text before commit.
  - Final flushing on both recordings: `and so my fellow Americans,`.
  - HTTP batch transcription: `And so, my fellow Americans,`.
  - Explicit unload terminating the owned runtime process.

The real-model check used the plugin API with pinned TypeWhisper SDK contracts
and a minimal test host. It did not exercise the running TypeWhisper UI or insert
dictation into another application. It is not a latency benchmark.

## Published native CI evidence

Repository: https://github.com/lrq3000/typewhisper-r2t2-crispasr

Successful native build and integration run:
https://github.com/lrq3000/typewhisper-r2t2-crispasr/actions/runs/37094516354

The tested source commit is `d21f50747c9ee5cc726703cf18cd0e6e487b08e7`.
All three jobs passed: Windows x64/CPU, macOS arm64/Metal and macOS x86_64/CPU.

On each Mac architecture:

- The pinned CrispASR runtime and native watchdog compiled successfully.
- **Eight Swift tests passed**, including real RFC6455 transport with fragmented
  UTF-8, final callback cancellation and cancellation at method entry.
- Both native watchdog acceptance tests passed: abrupt host exit and a surviving
  descendant that ignores SIGTERM after its group leader exits.
- Release compilation and SDK-symbol compatibility passed against the actual
  TypeWhisper 1.7.0 framework. CI exposed a newer auto-unload helper absent from
  that host; the plugin now builds against the released SDK at
  `c9958a59454b214f267a9d79fdbf6798b8a6d538` and retains the warm runtime until
  explicit unload or deactivation.
- `python tools/check_bundle.py` loaded the packaged bundle against that actual
  framework, discovered its principal class and SDK protocol identity, activated
  it, resolved bundled model metadata, created its settings view, persisted model
  selection and deactivated it successfully.
- Ad-hoc signed architecture-specific bundle ZIPs and SHA-256 files were produced.

All three published archives were downloaded and checked locally with
`python tools/check_archives.py .build/ci-packages-d21f507`. Exact SHA-256,
manifest/principal-class metadata, executable bits, runtime patch metadata,
license files and SDK exclusion passed.

`python tools/check_swift_syntax.py` also parses eleven Swift files locally; this
portable check supplements the native compiler results above.

## Remaining acceptance

The macOS bundle-loading probe uses the real host SDK framework in an isolated
test process; it does not exercise the full TypeWhisper application's UI, global
shortcuts, microphone permissions or insertion into another application. Real
Q4_K inference was checked on Windows, not yet on a physical Mac/Metal device.

For further native development, run on a Mac:

```sh
python3 -m pip install cmake
python3 tools/build.py runtime --acceleration metal --arch arm64
python3 tools/build.py macos
python3 tools/check_bundle.py
python3 -m unittest discover -s tests -v
```

For Intel use `--acceleration cpu --arch x86_64`. Then load the generated bundle
in an isolated TypeWhisper setup, confirm model download/selection, live text,
final insertion, restart and unload, and perform real-model Metal/Intel acceptance.

The native build/toolchain blocker is resolved through GitHub Actions. Full UI
and physical-device inference acceptance remain separate from these CI checks.
