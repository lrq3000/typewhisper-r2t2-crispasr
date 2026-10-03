# TypeWhisper R2T2 · CrispASR

Native TypeWhisper transcription plugins for **Windows x64** and **macOS 14+**,
using Confucius4-R2T2 through an automatically managed CrispASR process.
No Python, compiler, API account, or manually started server is needed for users.
Python and the platform toolchains are needed only to build from source.

## Features

- Local WAV transcription and live recording with pre-commit text updates.
- Q8_0 (2.51 GB) and Q4_K (1.49 GB), downloaded from pinned Hugging Face revisions.
- Streaming SHA-256 verification and atomic model installation; canceled or bad
  downloads preserve existing usable weights.
- A retained model between recordings; an explicit **Unload** action releases it.
- Native parent-exit supervision: Windows Job Objects and a macOS watchdog.
- HTTP and both WebSocket listeners restricted to `127.0.0.1` by a checked source patch.
- Thirty language hints, mapped to the model's canonical names. R2T2 is primarily
  optimized for Chinese and English. Translation and dictionary/hotword support
  are not advertised by this version.

The plugins use **CrispASR**, not the existing audio.cpp R2T2 plugin. Both are
separate integrations and have different plugin/provider IDs.

## Builds and validation

All three native CI builds pass: **Windows x64/CPU**, **macOS arm64/Metal**, and
**macOS x86_64/CPU**. The Mac builds include eight Swift tests, native watchdog
acceptance, SDK-symbol compatibility against TypeWhisper 1.7.0, and loading and
activating the packaged bundle against that host's actual SDK framework.

Download the platform archives and Windows offline installer from the artifacts
of a successful [GitHub Actions run](https://github.com/lrq3000/typewhisper-r2t2-crispasr/actions).
Archives include SHA-256 files, license text and setup instructions. Windows
also passed real Q4_K streaming and batch inference locally. See
[verification evidence](docs/verification.md) for tested scope and remaining UI
and macOS real-model acceptance.

## Install — Windows

Requires **TypeWhisper Windows 1.1 / WinUI**, which is currently the Daily
development line, and Windows x64 with AVX2. This is not a legacy WPF 1.0 plugin.

Build/download `R2T2CrispASR-windows-x64.zip` and the accompanying `Installer/`
folder. The Windows host uses an authoritative plugin package index, so simply
copying a DLL into a directory is insufficient. The supplied offline installer
uses the pinned upstream package-store implementation to validate the archive
and register it, preserving existing preferences and other packages.

Close TypeWhisper, then run:

```powershell
& ".\Installer\R2T2Installer.exe" ".\R2T2CrispASR-windows-x64.zip" "$env:LOCALAPPDATA\TypeWhisper-WinUI"
```

The final argument must point to your existing profile. The ordinary WinUI
development profile is `TypeWhisper-WinUI-DevUserData`; production/Daily release
builds use `TypeWhisper-WinUI`. An optional third argument supplies the host
version, defaulting to `1.1.0`. The installer needs .NET 10 Runtime, which the
TypeWhisper installer provides.

Start TypeWhisper, enable **R2T2 (CrispASR)** in Integrations, download a model,
choose **Use model**, and select it as your transcription engine. Live text uses
the host's live recording interface. VC++/OpenMP runtime DLLs are included in the
plugin runtime folder.

## Install — macOS

Use the archive matching the Mac architecture: Apple Silicon **arm64/Metal** or
Intel **x86_64/CPU**. Requires **TypeWhisper 1.7+**, macOS 14+, and the host's SDK
framework. Each generated bundle advertises only its actual built architecture.

Unzip and copy `R2T2CrispASR.bundle` into:

```text
~/Library/Application Support/TypeWhisper/Plugins/
```

Restart TypeWhisper. Open the plugin's settings, download Q8_0 or Q4_K, and choose
**Use model**. Download controls show progress and support cancellation. Select
**R2T2 (CrispASR)** as your engine. The build produces an ad-hoc-signed development
bundle; published notarized releases require the publisher's Developer ID signing.

## Build from source

Run all commands from this repository's working tree. Generated dependencies,
build logs and artifacts remain under `.deps/`, `.build/` and `dist/`.

### Windows

Install Python 3.11+, Visual Studio **2022 or 2026 C++ Build Tools** with a Windows
SDK, CMake, and .NET 10 SDK. A local .NET SDK can be bootstrapped without changing
the system installation:

```powershell
python -m pip install cmake
python tools/build.py prepare --bootstrap-dotnet
python tools/build.py runtime --acceleration cpu
python tools/build.py windows
python -m unittest discover -s tests -v
```

The result is `dist/R2T2CrispASR-windows-x64.zip`, its `.sha256`, and the offline
installer in `dist/Installer/`. Full compiler output is in `.build/build.log`.

CUDA is an optional source build: use `runtime --acceleration cuda` with an
installed CUDA Toolkit. CUDA runtime redistributables require separate packaging;
the validated/default Windows artifact is CPU-only.

### macOS

Install Xcode with a Swift 6 toolchain, Python 3.11+ and CMake. On Apple Silicon:

```sh
python3 -m pip install cmake
python3 tools/build.py runtime --acceleration metal --arch arm64
python3 tools/build.py macos
python3 -m unittest discover -s tests -v
```

On an Intel Mac, replace the runtime command with
`python3 tools/build.py runtime --acceleration cpu --arch x86_64`.
The plugin build runs `swift test` and builds its Release library, converts its
SDK dependency to the **host framework**, packages the bundle, signs the runtime
and bundle ad hoc, and writes an architecture-specific ZIP and SHA-256.
It does not embed a competing copy of the SDK.
Before packaging, it downloads the minimum host release into `.deps/` and checks
the plugin's SDK imports against that release using TypeWhisper's symbol checker.
`python3 tools/check_bundle.py` additionally loads and activates the packaged
bundle against the actual host framework, exercising settings and model selection.

### Real-model Windows acceptance

After building the runtime:

```powershell
.\.deps\dotnet\dotnet.exe run --project windows/Acceptance -c Release -- .build/acceptance-assets .deps/crispasr/samples/jfk.wav
```

Use `dotnet` instead of the local SDK path when .NET 10 SDK is installed globally.
This downloads Q4_K once, tests two recordings with pre-commit text and final
flushing, checks loopback listeners, exercises the HTTP batch path, and unloads
the child process. It uses four seconds of the upstream JFK sample and is an
acceptance check, not a speed benchmark. Assets are retained in the isolated
acceptance directory for repeat runs. Ordinary unit tests download no models.

## Runtime details

CrispASR is pinned to **v0.8.41 / 340d7085eaa5**. Both WebSocket listener binds
are changed from `INADDR_ANY` to `htonl(INADDR_LOOPBACK)`. Patching fails closed
if upstream code no longer matches. Native GPU/CPU dependencies are built from
the pinned checkout. User recordings go only to the private loopback runtime.

The live endpoint is the **vLLM-compatible `/v1/realtime`**, not the older
Whisper-only raw-PCM endpoint. The plugin sends text frames with base64 PCM16LE
mono 16 kHz, verifies `session.created.partial_transcription`, and explicitly
commits at recording end. Final transcripts replace draft text; server-triggered
30-second turns are retained. Disconnect, native fallback or incomplete coverage
fails the session. Cancellation stops the runtime instead of leaving inference
running after the transport has gone away.

R2T2's prefix-rollback algorithm retains transcript state but re-encodes accumulated
audio. The managed runtime uses a 320 ms scheduling step; this is not an
incremental encoder-cache implementation or a guarantee of real-time speed.
Changing model or language restarts the process because this server version fixes
the live language at startup. Concurrent requests are rejected with a busy message.

## Models and licensing

Model metadata, exact sizes, hashes and language names are in
[`assets/models.json`](assets/models.json). We use the CrispASR conversions at
[`cstr/confucius4-r2t2-GGUF`](https://huggingface.co/cstr/confucius4-r2t2-GGUF),
revision `6a9aa41833f577a7a2f5d0a2ed61d8250c0c57b4`, which carry the R2T2 streaming
metadata. Model weights are not included in plugin archives. `MODEL_LICENSE` is
downloaded and retained with each model. The weights use the **NetEase Youdao
Model Use License**, rather than CrispASR's MIT license.

> Any modifications made to the original model in this Derivative Work are not
> endorsed, warranted, or guaranteed by the original right-holder of the original
> model, and the original right-holder disclaims all liability related to this
> Derivative Work.

Plugin source is GPL-3.0-only, consistent with TypeWhisper. CrispASR's bundled
runtime retains its MIT license. Microsoft app-local runtime components retain
their redistributable licensing terms under Visual Studio.

## References

- [TypeWhisper plugin development](https://www.typewhisper.com/en/addons/develop/)
- [Windows portable package contract](https://github.com/TypeWhisper/typewhisper-win/blob/main/docs/PLUGIN-PACKAGES-1.1.md)
- [Cohere managed CrispASR example](https://github.com/TypeWhisper/typewhisper-mac/tree/main/TypeWhisperPluginSDK/Plugins/CohereLocalPlugin)
- [CrispASR realtime server](https://github.com/CrispStrobe/CrispASR/blob/v0.8.41/examples/server/realtime_server.cpp)
- [R2T2 native backend](https://github.com/CrispStrobe/CrispASR/blob/v0.8.41/examples/cli/crispasr_backend_qwen3.cpp)
