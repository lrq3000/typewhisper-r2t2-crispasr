"""Verify downloaded CI plugin archives, their manifests and exact checksums."""
import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import stat
import zipfile


class PackageInspector:
    def __init__(self, archive):
        self.archive = archive

    def verify(self):
        expected = self.archive.with_suffix(".zip.sha256").read_text().split()[0]
        with self.archive.open("rb") as stream:
            if hashlib.file_digest(stream, "sha256").hexdigest() != expected:
                raise ValueError(f"Checksum mismatch: {self.archive}")
        with zipfile.ZipFile(self.archive) as package:
            names = package.namelist()
            if any(name.endswith(".gguf") for name in names):
                raise ValueError("Weights must not be included in plugin archives")
            if "macos" in self.archive.name:
                prefix = "R2T2CrispASR.bundle/Contents/"
                resources = prefix + "Resources/"
                manifest = json.loads(package.read(resources + "manifest.json"))
                architecture = "arm64" if "arm64" in self.archive.name else "x86_64"
                if manifest["supportedArchitectures"] != [architecture]:
                    raise ValueError("Archive and manifest architecture differ")
                info = plistlib.loads(package.read(prefix + "Info.plist"))
                if info["NSPrincipalClass"] != manifest["principalClass"]:
                    raise ValueError("Bundle principal-class metadata differs")
                for path in (prefix + "MacOS/" + info["CFBundleExecutable"], resources + "Runtime/crispasr", resources + "Runtime/r2t2-watchdog"):
                    if not package.getinfo(path).external_attr >> 16 & stat.S_IXUSR:
                        raise ValueError(f"Missing executable bit: {path}")
                if any("TypeWhisperPluginSDK.framework/" in name for name in names):
                    raise ValueError("The plugin must use the host SDK framework")
            else:
                resources = ""
                manifest = json.loads(package.read("manifest.json"))
                package.getinfo(manifest["assemblyName"])
                if any(name.startswith("TypeWhisper.PluginSDK") for name in names):
                    raise ValueError("The plugin must use the host SDK assembly")
            for name in ("LICENSE", "COPYING", "README.md", "models.json"):
                package.getinfo(resources + name)
            runtime = json.loads(package.read(resources + "Runtime/runtime.json"))
            if not runtime["loopbackOnly"]:
                raise ValueError("Runtime lacks the loopback-only patch")
        print(f"Verified SHA-256, package metadata and contents: {self.archive.name}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    args = parser.parse_args()
    archives = list(args.directory.rglob("R2T2CrispASR-*.zip"))
    if not archives:
        raise ValueError("No plugin archives found")
    for archive in archives:
        PackageInspector(archive).verify()


if __name__ == "__main__":
    main()
