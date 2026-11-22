import json
from pathlib import Path
import tempfile
import subprocess
import sys
import unittest

from entrypoint import prepare


class RuntimeDirectories(unittest.TestCase):
    def test_fresh_runtime_paths_and_repeat(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            config = root / "engine.json"
            content = json.dumps({"log": str(root / "logs/nested/engine.log"),
                                  "env": {"MAYA_DS4_SLOT_DIR": str(root / "slots")}})
            config.write_text(content)
            for _ in range(2):
                prepare(config, {"TMPDIR": str(root / "tmp")})
            for directory in ("logs/nested", "slots", "tmp"):
                self.assertTrue((root / directory).is_dir())
            self.assertEqual(content, config.read_text())
            self.assertFalse((root / "logs/nested/engine.log").exists())

    def test_module_launch_preserves_working_directory(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "serve").mkdir()
            (root / "serve/__init__.py").write_text("")
            (root / "serve/server.py").write_text("from pathlib import Path; assert Path('logs').is_dir(); print('STARTED')")
            config = root / "engine.json"
            config.write_text(json.dumps({"log": str(root / "logs/engine.log")}))
            entrypoint = Path(__file__).with_name("entrypoint.py").resolve()
            result = subprocess.run([sys.executable, str(entrypoint), "--config", str(config)],
                                    cwd=root, capture_output=True, text=True, check=True)
            self.assertEqual("STARTED", result.stdout.strip())


if __name__ == "__main__":
    unittest.main()
