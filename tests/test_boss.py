"""boss.py spec checks with stub archives: no game data, SDK or dotnet.   python3 -m unittest discover tests"""
import json, sys, tempfile, unittest, xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(REPO / "tools/dungeon"))
import boss  # noqa: E402
import newmap  # noqa: E402


class StubArchive:
    """name -> text, served from a temp dir like newmap.Archive (has / under / file / fetch)."""

    def __init__(self, root, files):
        self.dir, self.path = Path(root), Path(root) / "stub.m2d"
        for n, t in files.items():
            (self.dir / n).parent.mkdir(parents=True, exist_ok=True)
            (self.dir / n).write_text(t, encoding="utf-8")
        self.names = list(files)

    def has(self, name):
        return name.lower() in {n.lower() for n in self.names}

    def under(self, prefix):
        return [n for n in self.names if n.lower().startswith(prefix.lower())]

    def fetch(self, *prefixes):
        pass

    def file(self, name):
        return self.dir / name if self.has(name) else None


ANIKEY = """<ms2>
<kfm name="boss_model">
 <seq name="Idle_A"></seq>
 <seq name="Attack_01_A"><key name="p0" time="0.5"/></seq>
 <seq name="Cast_A"></seq>
</kfm>
</ms2>
"""

SKILLS = """<ms2>
<skill id="50100001"><level value="1"><motion sequenceName="Attack_01_A"><attack point="p0"/></motion></level></skill>
<skill id="50100002"><level value="1"><motion sequenceName="Cast_A"><attack point="p0"/></motion></level></skill>
<skill id="50100003"><level value="1"><motion sequenceName="Missing_A"></motion></level></skill>
</ms2>
"""


class FakeGen:
    def __init__(self, tmp, spec):
        self.spec, self.notes = spec, []
        self.cx = StubArchive(tmp, {"anikeytext.xml": ANIKEY, "skilldata/501.xml": SKILLS})


def crow(ids):
    return f'<npc id="23009901"><model kfm="boss_model"/><skill ids="{ids}" levels="{",".join("1" for _ in ids.split(","))}"/></npc>'


class Sequences(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.b = boss.Bosses(FakeGen(self.tmp.name, {}))

    def tearDown(self):
        self.tmp.cleanup()

    def test_hit_key_present_passes(self):
        self.b.check_sequences(crow("50100001"), "bosses[23009901]", {50100001})

    def test_listed_skill_without_hit_key_fails(self):
        with self.assertRaisesRegex(newmap.SpecError, r"attack point p0 is no key of Cast_A"):
            self.b.check_sequences(crow("50100001,50100002"), "bosses[23009901]", {50100002})

    def test_inherited_skill_without_hit_key_is_a_note(self):
        self.b.check_sequences(crow("50100002"), "bosses[23009901]", set())
        self.assertTrue(any("never hit" in n for n in self.b.g.notes))

    def test_missing_sequence_fails(self):
        with self.assertRaisesRegex(newmap.SpecError, r"sequenceName Missing_A"):
            self.b.check_sequences(crow("50100003"), "bosses[23009901]", set())

    def test_unknown_kfm_fails(self):
        with self.assertRaisesRegex(newmap.SpecError, r"no row for kfm"):
            self.b.check_sequences(crow("50100001").replace("boss_model", "other"), "x", set())


class Ai(unittest.TestCase):
    def setUp(self):
        self.b = boss.Bosses(FakeGen(tempfile.gettempdir(), {}))

    def test_generated_ai_is_xml(self):
        b = {"skills": [[1, 1], [2, 1]], "ai": {"melee": 1, "specials": [{"idx": 2, "cooltime": 7000}]}}
        root = ET.fromstring(self.b.ai_text(b, "x"))
        self.assertEqual(root.tag, "npcAi")
        self.assertEqual({n.get("idx") for n in root.iter("node") if n.get("name") == "skill"}, {"1", "2"})

    def test_ai_idx_out_of_range_fails(self):
        with self.assertRaisesRegex(newmap.SpecError, r"outside the boss's 1 skills"):
            self.b.ai_text({"skills": [[1, 1]], "ai": {"melee": 1, "specials": [{"idx": 2}]}}, "x")

    def test_missing_ai_file_fails(self):
        with self.assertRaisesRegex(newmap.SpecError, r"ai file .* not found"):
            self.b.ai_text({"skills": [[1, 1]], "ai": {"file": "/nonexistent/ai.xml"}}, "x")


class Example(unittest.TestCase):
    def test_boss_arena_spec(self):
        spec = newmap.load_spec(REPO / "tools/dungeon/examples/boss-arena/spec.json")
        for b in spec["bosses"]:
            self.assertIn(int(b["id"]), boss.custom_npc_ids(spec))
            n = len(b["skills"])
            ai = b.get("ai") or {}
            for x in [int(ai.get("melee", 1))] + [int(s["idx"]) for s in ai.get("specials", [])]:
                self.assertTrue(1 <= x <= n)
        for s in spec["skills"]:
            keys = set(s.get("effects", {}))
            for m in s["motions"]:
                self.assertIn(len(m["from"]), (2, 3))
                if "effect" in m:
                    self.assertIn(m["effect"], keys)


if __name__ == "__main__":
    unittest.main()
