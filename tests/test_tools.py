"""python3 -m unittest discover tests   (needs no game data, SDK or dotnet)"""
import compileall, json, os, subprocess, sys, unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SETTINGS = ["MS2_CLIENT_DATA", "MS2_SERVER_DATA", "MS2_SERVER_DIR", "MS2_DOTNET_ROOT", "MS2_TOKBUILD", "MS2_CP3D",
            "MS2_PATHENGINE_SDK", "MS2_MSVC_ROOT", "MS2_GAMEBRYO_APPDIR", "MS2_TRIGGER_CACHE", "MS2_OUT"]
EXAMPLES = ["custom-warehouse", "combat-short", "new-arena"]


def run(*args, cwd=REPO):
    env = {k: v for k, v in os.environ.items() if k not in SETTINGS}
    return subprocess.run([sys.executable, *map(str, args)], cwd=cwd, env=env, capture_output=True, text=True)


@unittest.skipIf((REPO / "modtools.toml").exists(), "needs no modtools.toml")
class MissingSettings(unittest.TestCase):
    def test_build_names_the_setting(self):
        r = run("tools/dungeon/build.py", "tools/dungeon/examples/combat-short/spec.json", "-o", REPO / "out/test-missing")
        self.assertNotEqual(r.returncode, 0)
        self.assertRegex(r.stdout + r.stderr, r"MS2_(CLIENT|SERVER)_DATA is not set")
        self.assertNotIn("Traceback", r.stdout + r.stderr)

    def test_new_map_names_the_setting(self):
        r = run("tools/dungeon/mkxblock.py", "tools/dungeon/examples/new-arena/spec.json", "-o", REPO / "out/test-missing.xblock")
        self.assertNotEqual(r.returncode, 0)
        self.assertRegex(r.stdout + r.stderr, r"MS2_(CLIENT|SERVER)_DATA is not set")

    def test_sdk_tools_name_the_setting(self):
        r = run("tools/mapedit/knownguids.py", "-o", REPO / "out/test-guids.json")
        self.assertIn("MS2_GAMEBRYO_APPDIR is not set", r.stdout + r.stderr)

    def test_config_lists_all(self):
        out = run("tools/modtools_config.py").stdout
        for name in SETTINGS:
            self.assertIn(name, out)

    def test_config_single_unset(self):
        self.assertEqual(run("tools/modtools_config.py", "MS2_CP3D").returncode, 1)


class Sources(unittest.TestCase):
    def test_python_compiles(self):
        self.assertTrue(compileall.compile_dir(REPO / "tools", quiet=2, force=True))

    def test_examples_are_valid_json(self):
        for name in EXAMPLES:
            spec = json.loads((REPO / f"tools/dungeon/examples/{name}/spec.json").read_text(encoding="utf-8"))
            self.assertIn("map_id", spec)


class TriggerLint(unittest.TestCase):
    def test_example_triggers_pass_strict(self):
        for name in EXAMPLES:
            r = run("tools/trigger/lint.py", f"tools/dungeon/examples/{name}/trigger", "--strict")
            self.assertEqual(r.returncode, 0, f"{name}: {r.stdout}{r.stderr}")


if __name__ == "__main__":
    unittest.main()
