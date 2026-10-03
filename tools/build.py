"""Reproducible source/runtime/plugin builds. Python is not a runtime dependency."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import sysconfig
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
DEPS = ROOT / ".deps"
BUILD = ROOT / ".build"
PINS = {
    "crispasr": ("CrispStrobe/CrispASR", "340d7085eaa53c40a46dcb73a6d3d0448a480006", None),
    "typewhisper-win": ("TypeWhisper/typewhisper-win", "a0ec3220118ab30d0d009cdae34aba5dfe262b7e", "src/TypeWhisper.PluginSDK"),
    "typewhisper-mac": ("TypeWhisper/typewhisper-mac", "ea85d180404d169260e422ce52784336d6f1739a", "TypeWhisperPluginSDK/Sources/TypeWhisperPluginSDK"),
}


def run(arguments, cwd=ROOT, env=None):
    """Keep compiler output bounded in the harness; retain the complete log."""
    BUILD.mkdir(exist_ok=True)
    with (BUILD / "build.log").open("a", encoding="utf-8") as log:
        log.write("\nCOMMAND " + repr([str(x) for x in arguments]) + "\n")
        log.flush()
        result = subprocess.run([str(x) for x in arguments], cwd=cwd, env=env,
                                stdout=log, stderr=subprocess.STDOUT)
    if result.returncode:
        lines = (BUILD / "build.log").read_text(encoding="utf-8", errors="replace").splitlines()
        print("\n".join(lines[-45:]), file=sys.stderr)
        raise RuntimeError(f"Command failed ({result.returncode}); full log: {BUILD / 'build.log'}")


def patch_loopback(source):
    """Fail closed if upstream changes socket initialization; never guess a patch."""
    original = "addr.sin_addr.s_addr = INADDR_ANY;"
    patched = "addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);"
    edits = []
    for name in ("realtime_server.cpp", "ws_stream.cpp"):
        path = source / "examples" / "server" / name
        text = path.read_text(encoding="utf-8")
        if text.count(original) == 1 and patched not in text:
            text = text.replace(original, patched)
        elif text.count(patched) != 1 or original in text:
            raise ValueError(f"Unexpected socket initialization in {path}")
        edits.append((path, text.replace("ws://0.0.0.0:", "ws://127.0.0.1:")))
    # Validate every precondition before changing either file.
    for path, text in edits:
        path.write_text(text, encoding="utf-8", newline="\n")


def prepare(name):
    repository, revision, sparse = PINS[name]
    destination = DEPS / name
    DEPS.mkdir(exist_ok=True)
    if not (destination / ".git").exists():
        destination.mkdir(exist_ok=True)
        run(["git", "init", "-q", destination])
        run(["git", "remote", "add", "origin", f"https://github.com/{repository}.git"], destination)
        run(["git", "fetch", "--quiet", "--depth=1", "origin", revision], destination)
        if sparse:
            run(["git", "sparse-checkout", "set", sparse], destination)
        run(["git", "checkout", "--quiet", "--detach", revision], destination)
        if name == "crispasr":
            run(["git", "submodule", "update", "--init", "--depth=1"], destination)
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=destination, text=True).strip()
    if actual != revision:
        raise ValueError(f"Pinned revision mismatch: {name}: {actual}")
    if name == "typewhisper-win":
        run(["git", "sparse-checkout", "add", "src/TypeWhisper.PluginHost"], destination)
    elif name == "typewhisper-mac":
        run(["git", "sparse-checkout", "add", "scripts"], destination)
    print(f"Prepared {name} {revision[:12]}")
    return destination


def tool(name):
    executable = shutil.which(name)
    if executable:
        return executable
    candidate = Path(sysconfig.get_path("scripts")) / (name + (".exe" if os.name == "nt" else ""))
    if candidate.exists():
        return str(candidate)
    raise RuntimeError(f"Install {name} first (CMake: python -m pip install cmake)")


def redist_directory(cache):
    # Visual Studio generators omit CMAKE_CXX_COMPILER from the cache, but
    # record the linker. Both are in the same selected MSVC toolchain folder.
    match = re.search(r"^CMAKE_LINKER:FILEPATH=(.+)$", cache, re.MULTILINE)
    if not match:
        raise RuntimeError("MSVC linker path is absent from CMake's cache")
    linker = Path(match[1].strip())
    return linker.parents[6] / "Redist" / "MSVC" / linker.parents[3].name / "x64"


def dotnet(bootstrap=False):
    local = DEPS / "dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    if not local.exists() and bootstrap:
        if os.name != "nt" or platform.machine().lower() not in ("amd64", "x86_64"):
            raise RuntimeError("Automatic SDK bootstrap supports Windows x64; install .NET 10 SDK on other platforms")
        with urllib.request.urlopen("https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json") as response:
            metadata = json.load(response)
        sdk = next(s for r in metadata["releases"] for s in r.get("sdks", [r["sdk"]]) if s["version"] == "10.0.100")
        asset = next(a for a in sdk["files"] if a["rid"] == "win-x64" and a["name"].endswith(".zip"))
        archive = DEPS / "dotnet-sdk.zip"
        DEPS.mkdir(exist_ok=True)
        urllib.request.urlretrieve(asset["url"], archive)
        digest = hashlib.file_digest(archive.open("rb"), "sha512").hexdigest()
        if digest.lower() != asset["hash"].lower():
            archive.unlink()
            raise ValueError(".NET SDK checksum mismatch")
        with zipfile.ZipFile(archive) as package:
            package.extractall(local.parent)
        archive.unlink()
    executable = str(local) if local.exists() else tool("dotnet")
    version = subprocess.check_output([executable, "--version"], text=True).strip()
    if int(version.split(".")[0]) < 10:
        raise RuntimeError(".NET 10 SDK is required; use --bootstrap-dotnet for isolated Windows installation")
    return executable


def build_runtime(acceleration="cpu", arch=None):
    source = prepare("crispasr")
    patch_loopback(source)
    native_build = BUILD / f"runtime-{acceleration}"
    cmake = tool("cmake")
    arguments = [cmake, "-S", source, "-B", native_build,
                 "-DCMAKE_BUILD_TYPE=Release", "-DBUILD_SHARED_LIBS=OFF",
                 "-DCRISPASR_BUILD_TESTS=OFF", "-DCRISPASR_BUILD_SERVER=ON",
                 "-DCRISPASR_ALL_WARNINGS=OFF", "-DGGML_NATIVE=OFF",
                 f"-DGGML_CUDA={'ON' if acceleration == 'cuda' else 'OFF'}",
                 f"-DGGML_METAL={'ON' if acceleration == 'metal' else 'OFF'}"]
    if os.name == "nt":
        vswhere = Path(os.environ.get("ProgramFiles(x86)", "C:/Program Files (x86)")) / "Microsoft Visual Studio/Installer/vswhere.exe"
        version = subprocess.check_output([str(vswhere), "-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationVersion"], text=True).strip()
        generator = {"17": "Visual Studio 17 2022", "18": "Visual Studio 18 2026"}.get(version.split(".")[0])
        if not generator:
            raise RuntimeError("Visual Studio 2022/2026 C++ Build Tools are required")
        arguments += ["-G", generator, "-A", "x64"]
    if sys.platform == "darwin":
        arguments += ["-DCMAKE_OSX_DEPLOYMENT_TARGET=14.0", f"-DCMAKE_OSX_ARCHITECTURES={arch or platform.machine()}"]
    run(arguments)
    run([cmake, "--build", native_build, "--config", "Release", "--target", "crispasr-cli", "--parallel", "4"])
    output = BUILD / "runtime"
    output.mkdir(exist_ok=True)
    executable = "crispasr.exe" if os.name == "nt" else "crispasr"
    binaries = list((native_build / "bin").rglob(executable))
    if len(binaries) != 1:
        raise RuntimeError(f"Expected one runtime executable, found {binaries}")
    shutil.copy2(binaries[0], output / executable)
    for library in binaries[0].parent.glob("*.dll"):
        shutil.copy2(library, output / library.name)
    if os.name == "nt":
        # App-local CRT/OpenMP DLLs avoid making end users install a compiler or
        # a separate VC++ redistributable. Only Release redistributable folders
        # are searched; debug_nonredist and OneCore binaries are excluded.
        cache = (native_build / "CMakeCache.txt").read_text(encoding="utf-8")
        redist = redist_directory(cache)
        libraries = list(redist.glob("Microsoft.VC*.CRT/*.dll")) + list(redist.glob("Microsoft.VC*.OpenMP/*.dll"))
        if not libraries:
            raise RuntimeError(f"Visual C++ app-local redistributable DLLs not found: {redist}")
        for library in libraries:
            shutil.copy2(library, output / library.name)
    shutil.copy2(source / "LICENSE", output / "CrispASR-LICENSE")
    (output / "runtime.json").write_text(json.dumps({"version": "0.8.41", "loopbackOnly": True,
        "revision": PINS["crispasr"][1], "acceleration": acceleration, "architecture": arch or platform.machine()}))
    if sys.platform == "darwin":
        run(["clang++", "-std=c++17", "-O2", ROOT / "runtime" / "watchdog.cpp", "-o", output / "r2t2-watchdog"])
    print(f"Runtime ready: {output}")
    return output


def build_windows(bootstrap=False):
    prepare("typewhisper-win")
    sdk = dotnet(bootstrap)
    run([sdk, "test", ROOT / "windows" / "Tests", "-c", "Release", "-v", "quiet"])
    staging = BUILD / "windows-package"
    if staging.exists():
        shutil.rmtree(staging)
    run([sdk, "publish", ROOT / "windows" / "R2T2CrispAsr.csproj", "-c", "Release", "-o", staging, "-v", "quiet"])
    for path in staging.glob("TypeWhisper.PluginSDK*"):
        path.unlink()  # Contract identity belongs to the host, not this package.
    runtime = BUILD / "runtime"
    if not (runtime / "runtime.json").exists():
        raise RuntimeError("Build the patched runtime first: python tools/build.py runtime")
    shutil.copytree(runtime, staging / "Runtime", dirs_exist_ok=True)
    for name in ("LICENSE", "COPYING", "README.md"):
        shutil.copy2(ROOT / name, staging / name)
    destination = ROOT / "dist"
    destination.mkdir(exist_ok=True)
    archive = shutil.make_archive(str(destination / "R2T2CrispASR-windows-x64"), "zip", staging)
    write_checksum(Path(archive))
    run([sdk, "publish", ROOT / "windows" / "Installer", "-c", "Release", "-r", "win-x64", "--self-contained", "false",
         "-o", destination / "Installer", "-v", "quiet"])
    print(f"Plugin package ready: {archive}")


def build_macos():
    source = prepare("typewhisper-mac")
    # The SDK target has no external dependencies. Build only those upstream
    # sources, avoiding resolution of unrelated MLX/ONNX plugin dependencies.
    package = DEPS / "mac-sdk"
    package.mkdir(exist_ok=True)
    sources = package / "Sources"
    if not sources.exists():
        sources.symlink_to(source / "TypeWhisperPluginSDK" / "Sources", target_is_directory=True)
    (package / "Package.swift").write_text('''// swift-tools-version: 6.0
import PackageDescription
let package = Package(name: "TypeWhisperPluginSDK", platforms: [.macOS(.v14)],
    products: [.library(name: "TypeWhisperPluginSDK", type: .dynamic, targets: ["TypeWhisperPluginSDK"])],
    targets: [.target(name: "TypeWhisperPluginSDK")])
''')
    run(["swift", "test", "--package-path", ROOT / "macos"])
    # Match the host SDK's non-resilient compilation mode. Enabling library
    # evolution globally also changes the SDK dependency's protocol ABI.
    run(["swift", "build", "--package-path", ROOT / "macos", "-c", "release"])
    output = subprocess.check_output(["swift", "build", "--package-path", str(ROOT / "macos"), "-c", "release", "--show-bin-path"], text=True).strip()
    bundle = ROOT / "dist" / "R2T2CrispASR.bundle" / "Contents"
    (bundle / "MacOS").mkdir(parents=True, exist_ok=True)
    (bundle / "Resources").mkdir(exist_ok=True)
    shutil.copy2(Path(output) / "libR2T2CrispASR.dylib", bundle / "MacOS" / "R2T2CrispASR")
    shutil.copy2(ROOT / "macos" / "Info.plist", bundle / "Info.plist")
    shutil.copy2(ROOT / "macos" / "manifest.json", bundle / "Resources" / "manifest.json")
    manifest = json.loads((bundle / "Resources" / "manifest.json").read_text())
    manifest["supportedArchitectures"] = [platform.machine()]
    (bundle / "Resources" / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
    shutil.copy2(ROOT / "assets" / "models.json", bundle / "Resources" / "models.json")
    for name in ("LICENSE", "COPYING", "README.md"):
        shutil.copy2(ROOT / name, bundle / "Resources" / name)
    shutil.copytree(BUILD / "runtime", bundle / "Resources" / "Runtime", dirs_exist_ok=True)
    runtime_metadata = json.loads((BUILD / "runtime" / "runtime.json").read_text())
    if runtime_metadata["architecture"] != platform.machine():
        raise RuntimeError("The runtime and Swift plugin must target the same Mac architecture")
    binary = bundle / "MacOS" / "R2T2CrispASR"
    # The app exports a FRAMEWORK, whereas SwiftPM links a temporary dylib.
    # Redirect the dependency to the host framework; shipping a second SDK would
    # give protocol/type identity to a different image and break bundle loading.
    linked = subprocess.check_output(["otool", "-L", str(binary)], text=True)
    sdk_dependency = next(line.strip().split(" ")[0] for line in linked.splitlines()
                          if "libTypeWhisperPluginSDK.dylib" in line)
    run(["install_name_tool", "-change", sdk_dependency,
         "@rpath/TypeWhisperPluginSDK.framework/Versions/A/TypeWhisperPluginSDK", binary])
    run(["install_name_tool", "-add_rpath", "@executable_path/../Frameworks", binary])
    verify_host_sdk(binary, Path(output) / "libTypeWhisperPluginSDK.dylib", source)
    # A development ad-hoc signature is replaced with Developer ID for releases.
    run(["codesign", "--force", "--sign", "-", bundle / "Resources" / "Runtime" / "crispasr"])
    run(["codesign", "--force", "--sign", "-", bundle / "Resources" / "Runtime" / "r2t2-watchdog"])
    run(["codesign", "--force", "--sign", "-", bundle.parent])
    archive = ROOT / "dist" / f"R2T2CrispASR-macos-{platform.machine()}.zip"
    run(["ditto", "-c", "-k", "--keepParent", bundle.parent, archive])
    write_checksum(archive)
    print(f"Plugin bundle ready: {bundle.parent}")


def verify_host_sdk(binary, build_sdk, source):
    """Check imports against the real minimum host, not just a source SDK build."""
    host = DEPS / "minimum-host"
    if not host.exists():
        archive = DEPS / "TypeWhisper-v1.7.0.zip"
        urllib.request.urlretrieve("https://github.com/TypeWhisper/typewhisper-mac/releases/download/v1.7.0/TypeWhisper-v1.7.0.zip", archive)
        with zipfile.ZipFile(archive) as package:
            package.extractall(host)
        archive.unlink()
    matches = list(host.glob("**/TypeWhisperPluginSDK.framework/Versions/A/TypeWhisperPluginSDK"))
    if len(matches) != 1:
        raise RuntimeError("Minimum host release does not contain exactly one TypeWhisper SDK framework")
    run([sys.executable, source / "scripts" / "check_plugin_sdk_symbol_compatibility.py",
         "--plugin-binary", binary, "--build-sdk", build_sdk, "--host-sdk", matches[0], "--min-host-version", "1.7.0"])


def write_checksum(archive):
    with archive.open("rb") as data:
        digest = hashlib.file_digest(data, "sha256").hexdigest()
    archive.with_suffix(archive.suffix + ".sha256").write_text(f"{digest}  {archive.name}\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["prepare", "runtime", "windows", "macos"])
    parser.add_argument("--acceleration", choices=["cpu", "cuda", "metal"], default="cpu")
    parser.add_argument("--arch", choices=["arm64", "x86_64"])
    parser.add_argument("--platform", choices=["windows", "macos"])
    parser.add_argument("--bootstrap-dotnet", action="store_true")
    options = parser.parse_args()
    if options.action == "prepare":
        prepare("typewhisper-win" if (options.platform or ("windows" if os.name == "nt" else "macos")) == "windows" else "typewhisper-mac")
        prepare("crispasr")
        if options.bootstrap_dotnet:
            print(f".NET SDK: {dotnet(True)}")
    elif options.action == "runtime":
        build_runtime(options.acceleration, options.arch)
    elif options.action == "windows":
        build_windows(options.bootstrap_dotnet)
    else:
        build_macos()


if __name__ == "__main__":
    main()
