import os
from pathlib import Path
import subprocess
import signal
import sys
import tempfile
import time
import unittest

ROOT = Path(__file__).resolve().parents[1]


@unittest.skipUnless(sys.platform == "darwin", "Native macOS watchdog acceptance")
class WatchdogTests(unittest.TestCase):
    def assert_stopped(self, child):
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            try:
                os.kill(child, 0)
            except ProcessLookupError:
                return
            time.sleep(0.1)
        self.fail("Watchdog left an inference child running")

    def test_child_stops_after_parent_exits_without_cleanup(self):
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "child.pid"
            parent = subprocess.Popen([sys.executable, __file__, "--parent", str(marker)])
            parent.wait(timeout=10)
            self.assertEqual(parent.returncode, 0)
            child = int(marker.read_text())
            self.assert_stopped(child)

    def test_surviving_descendant_is_killed_after_leader_exits(self):
        with tempfile.TemporaryDirectory() as directory:
            marker = Path(directory) / "grandchild.pid"
            watchdog = ROOT / ".build" / "runtime" / "r2t2-watchdog"
            parent = subprocess.Popen([str(watchdog), str(os.getpid()), sys.executable, __file__, "--leader-exits", str(marker)])
            parent.wait(timeout=10)
            self.assertEqual(parent.returncode, 0)
            self.assert_stopped(int(marker.read_text()))


if __name__ == "__main__":
    if len(sys.argv) > 1 and sys.argv[1] == "--parent":
        marker = Path(sys.argv[2])
        watchdog = ROOT / ".build" / "runtime" / "r2t2-watchdog"
        subprocess.Popen([str(watchdog), str(os.getpid()), sys.executable, __file__, "--child", str(marker)],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        deadline = time.monotonic() + 5
        while not marker.exists() and time.monotonic() < deadline:
            time.sleep(0.05)
        os._exit(0 if marker.exists() else 1)
    elif len(sys.argv) > 1 and sys.argv[1] == "--leader-exits":
        marker = Path(sys.argv[2])
        subprocess.Popen([sys.executable, __file__, "--stubborn-child", str(marker)])
        deadline = time.monotonic() + 5
        while not marker.exists() and time.monotonic() < deadline:
            time.sleep(0.05)
        sys.exit(0 if marker.exists() else 1)
    elif len(sys.argv) > 1 and sys.argv[1] in ("--child", "--stubborn-child"):
        if sys.argv[1] == "--stubborn-child":
            signal.signal(signal.SIGTERM, signal.SIG_IGN)
        Path(sys.argv[2]).write_text(str(os.getpid()))
        time.sleep(30)
    else:
        unittest.main()
