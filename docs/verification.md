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

## macOS evidence and remaining checks

- `python tools/check_swift_syntax.py` parses all ten Swift files with
  tree-sitter-swift. This checks syntax, **not native type checking or SDK ABI**.
- A read-only review checked Swift/session ownership, bundle linkage and native
  supervision. Corrections include final-callback cancellation, entry-time
  cancellation cleanup, host-matching SDK compilation mode and descendant cleanup.
- Swift tests include a real RFC6455 peer with fragmented UTF-8 and cancellation
  regressions. Native watchdog tests exercise abrupt parent exit and descendants
  that ignore SIGTERM. These tests have **not run on this Windows machine**.
- The macOS build matrix targets Apple Silicon/Metal and Intel/CPU. Packaging
  rewrites SDK linkage to the host framework and invokes TypeWhisper's upstream
  SDK-symbol compatibility checker against the actual minimum host release.
  This workflow is supplied but has **not been executed or published**.

To finish native macOS verification, run on a Mac:

```sh
python3 -m pip install cmake
python3 tools/build.py runtime --acceleration metal --arch arm64
python3 tools/build.py macos
python3 -m unittest discover -s tests -v
```

For Intel use `--acceleration cpu --arch x86_64`. Then load the generated bundle
in an isolated TypeWhisper setup, confirm model download/selection, live text,
final insertion, restart and unload, and perform real-model Metal/Intel acceptance.

**Remaining blocker:** access to a macOS native runner/toolchain. No macOS binary
or native macOS success is claimed from Windows-only evidence.
