# R2T2 via managed CrispASR

Approved in the conversation on 2026-10-03. Implement without another design gate.

## Architecture

Ship native Swift/macOS and C#/.NET/Windows TypeWhisper transcription plugins.
Each owns a pinned CrispASR child process; no Python or user-managed server is
required at runtime. Package native runtime binaries with the plugin; download
only model weights on explicit user action. Python is a development/build tool.

Pin CrispASR v0.8.41, commit 340d7085eaa53c40a46dcb73a6d3d0448a480006.
Patch both WebSocket listeners from INADDR_ANY to htonl(INADDR_LOOPBACK), with
matching log messages. Build CPU on Windows x64, Metal on macOS arm64 and CPU
on macOS x86_64. CUDA is an optional Windows build variant, requiring its toolkit.
Do not advertise Windows ARM64 without a compiled and tested runtime.

Pin TypeWhisper Windows SDK a0ec3220118ab30d0d009cdae34aba5dfe262b7e and
macOS SDK ea85d180404d169260e422ce52784336d6f1739a. Use upstream source contracts;
do not distribute a competing SDK assembly/framework with the plugin.

## Models

Use cstr/confucius4-r2t2-GGUF revision
6a9aa41833f577a7a2f5d0a2ed61d8250c0c57b4: Q4_K and Q8_0, with exact size and
SHA-256 from Hugging Face LFS metadata. Download MODEL_LICENSE alongside each
model. Stream downloads to temporary files, verify, and atomically promote them.
Preserve existing usable weights on cancellation or failure. Selection persists
in host-provided plugin preferences. Expose supported languages and no translation
or dictionary capabilities that the realtime endpoint cannot honor.

## Protocol and lifecycle

HTTP /health establishes process readiness. Batch transcription uses
POST /v1/audio/transcriptions with WAV and JSON response. Live transcription uses
the vLLM-compatible /v1/realtime WebSocket at raw-WebSocket port + 1, JSON
input_audio_buffer.append with base64 PCM16LE mono 16 kHz, and commit to finish.
Require session.created.partial_transcription == true. Deltas are previews;
completed.transcript replaces the current preview and commits a turn. Preserve
all completed turns, including server-triggered 30-second turns. Track processed
sample counts so automatic completions are not mistaken for the final commit.
An interrupted or timed-out stream fails, never returns a partial success.

The process configuration fixes the realtime language; restart when language,
model or acceleration changes. Retain the process for repeated dictations with
the same configuration. One operation/session owns the model at a time. Reject
overlapping operations rather than share mutable R2T2 native context. Cancellation
closes transport and stops the runtime to prevent abandoned inference. Bound
messages/log tails, use timeouts, and release ownership on every error path.

Use Windows kill-on-close Job Objects and a small native macOS watchdog to stop
the child after unexpected host exit. Pass arguments as arrays, never shell text.

## Distribution and validation

Produce a Windows plugin ZIP with manifest and dependencies at archive root, and
a macOS .bundle with principal class, SDK compatibility metadata and embedded
runtime. Document prerequisites, installation, build commands, and signing.
Keep tests/reproduction programs in the repository. Test multi-turn completion,
UTF-8, fragmented messages, failed handshake, disconnect, cancellation, bad
downloads, loopback patch preconditions and process shutdown. Add CI for both
platforms. A source audit is not evidence of live inference: report separately
which real-model and native host checks actually ran.
