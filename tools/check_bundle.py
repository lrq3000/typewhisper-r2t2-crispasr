"""Compile and run a plugin-loading probe linked to the minimum host's SDK."""
from pathlib import Path
import subprocess
from build import ROOT, BUILD, DEPS, run


def main():
    output = Path(subprocess.check_output(["swift", "build", "--package-path", str(ROOT / "macos"), "-c", "release", "--show-bin-path"], text=True).strip())
    frameworks = list((DEPS / "minimum-host").glob("**/TypeWhisperPluginSDK.framework/Versions/A/TypeWhisperPluginSDK"))
    if len(frameworks) != 1:
        raise RuntimeError("Build the macOS plugin and validate its minimum host first")
    framework = frameworks[0]
    executable = BUILD / "check-bundle"
    run(["swiftc", "-parse-as-library", "-I", output / "Modules", "-L", output, "-lTypeWhisperPluginSDK",
         "-Xlinker", "-rpath", "-Xlinker", framework.parents[3], ROOT / "tools" / "check_bundle.swift", "-o", executable])
    linked = subprocess.check_output(["otool", "-L", str(executable)], text=True)
    sdk = next(line.strip().split(" ")[0] for line in linked.splitlines() if "libTypeWhisperPluginSDK.dylib" in line)
    run(["install_name_tool", "-change", sdk, "@rpath/TypeWhisperPluginSDK.framework/Versions/A/TypeWhisperPluginSDK", executable])
    # A new probe directory prevents preferences or cached weights affecting results.
    import tempfile
    with tempfile.TemporaryDirectory(prefix="bundle-check-", dir=BUILD) as directory:
        run([executable, ROOT / "dist" / "R2T2CrispASR.bundle", directory])
    print("Native plugin bundle loading check passed against the real TypeWhisper 1.7.0 SDK")


if __name__ == "__main__":
    main()
