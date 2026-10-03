import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
ARCHIVE = ROOT / "dist" / "R2T2CrispASR-windows-x64.zip"


@unittest.skipUnless(ARCHIVE.exists(), "Build the Windows package first")
class WindowsPackageTests(unittest.TestCase):
    def test_package_has_contract_at_root_and_no_sdk_or_models(self):
        with zipfile.ZipFile(ARCHIVE) as package:
            names = package.namelist()
            self.assertIn("manifest.json", names)
            self.assertIn("R2T2CrispASR.dll", names)
            self.assertIn("R2T2CrispASR.deps.json", names)
            self.assertIn("Runtime/crispasr.exe", names)
            self.assertIn("Runtime/msvcp140.dll", names)
            self.assertIn("Runtime/vcomp140.dll", names)
            self.assertFalse(any("TypeWhisper.PluginSDK" in name or name.endswith(".gguf") for name in names))
            self.assertFalse(any("Tests" in name or "Acceptance" in name for name in names))
            manifest = json.loads(package.read("manifest.json"))
            self.assertEqual(manifest["assemblyName"], "R2T2CrispASR.dll")
            self.assertEqual(manifest["supportedArchitectures"], ["x64"])
            self.assertTrue(json.loads(package.read("Runtime/runtime.json"))["loopbackOnly"])

    def test_published_checksum_matches_the_exact_archive(self):
        expected = ARCHIVE.with_suffix(".zip.sha256").read_text().split()[0]
        with ARCHIVE.open("rb") as data:
            self.assertEqual(hashlib.file_digest(data, "sha256").hexdigest(), expected)

    @unittest.skipUnless(os.name == "nt", "Windows package-store acceptance")
    def test_upstream_store_installs_archive_without_touching_preferences(self):
        local_dotnet = ROOT / ".deps" / "dotnet" / "dotnet.exe"
        dotnet = str(local_dotnet) if local_dotnet.exists() else shutil.which("dotnet")
        self.assertIsNotNone(dotnet)
        result = subprocess.run([dotnet, str(ROOT / "dist" / "Installer" / "R2T2Installer.dll"), "--verify-package", str(ARCHIVE)],
                                capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("preference preservation PASS", result.stdout)


if __name__ == "__main__":
    unittest.main()
