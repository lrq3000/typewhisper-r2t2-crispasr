import importlib.util
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class RuntimePatchTests(unittest.TestCase):
    def setUp(self):
        path = ROOT / "tools" / "build.py"
        self.assertTrue(path.exists(), "Runtime builder has not been implemented")
        spec = importlib.util.spec_from_file_location("builder", path)
        self.builder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.builder)

    def test_both_listeners_are_loopback_and_patch_is_idempotent(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("realtime_server.cpp", "ws_stream.cpp"):
                path = root / "examples" / "server" / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text('addr.sin_addr.s_addr = INADDR_ANY;\n"ws://0.0.0.0:%d"\n')
            self.builder.patch_loopback(root)
            self.builder.patch_loopback(root)
            for path in (root / "examples" / "server").glob("*.cpp"):
                text = path.read_text()
                self.assertIn("htonl(INADDR_LOOPBACK)", text)
                self.assertNotIn("INADDR_ANY", text)
                self.assertNotIn("0.0.0.0", text)

    def test_unexpected_source_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("realtime_server.cpp", "ws_stream.cpp"):
                path = root / "examples" / "server" / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_text("unknown binding implementation")
            with self.assertRaises(ValueError):
                self.builder.patch_loopback(root)

    def test_visual_studio_cache_does_not_need_a_compiler_entry(self):
        cache = "CMAKE_LINKER:FILEPATH=C:/Kit/VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/link.exe\n"
        self.assertEqual(self.builder.redist_directory(cache), Path("C:/Kit/VC/Redist/MSVC/14.51.36231/x64"))


if __name__ == "__main__":
    unittest.main()
