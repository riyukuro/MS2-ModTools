#!/usr/bin/env python3
"""boss.py: custom bosses for tools/dungeon (called by newmap.py when the spec has "bosses" or "skills").

Every row is cloned from a shipped one, as in newmap.py: a boss is a shipped NPC under a new id with its own name,
level, stats, model, skill list and AI; a custom skill is built from motions of shipped skill levels with their
attack range / damage / motion attributes changed and an effect file of its own.  Written:

  client-xml   npcdata/<id[:3]>.xml       the clone's <npc> row appended to the file of its id prefix
               skilldata/<id[:3]>.xml     the custom <skill> row, same rule
               string/<lang>/npcname.xml, skillname.xml   one <key> row per id
               effect/<file>              the custom effect: the <effect> elements of shipped effect files,
                                          rescaled / delayed per part
  server-xml   npc/<a>/<b>/<id>.xml, skill/<a>/<id>.xml, string/en/npcname.xml, skillname.xml
  server       AI/<path>                  the boss AI: generated (melee + specials) or the spec's own file

Spec (see examples/boss-arena/spec.json):

  "skills": [{"id": 50109901, "names": {"en": "..."},
              "motions": [{"from": [skill, level, motion index (0)],
                           "motion": {attr: v}, "attack": {attr: v}, "range": {attr: v}, "damage": {attr: v},
                           "effect": "<effects key>", "hit": false}, ...],   hit false: drop the motion's
                                                                  attacks (a telegraph / wind-up motion)
              "detect": {attr: v},                       detectProperty of the new level (default: first motion's)
              "effects": {"<key>": {"file": "custom/eff_x.xml",
                                    "parts": [{"from": "NPC/Skill/.../Eff_y.xml", "scale": 2.0, "delay": 0.0,
                                               "duration": 2.0, "translate": [x, y, z],
                                               "only": "<regex an <effect> element must match>"}, ...]}}}]
              (a part's "duration" also replaces loop="true", so a looping ground guide circle ends on time)
  "bosses": [{"id": 23009901, "clone_from": 23000039, "names": {"en": "..."},
              "level": 10, "model": {"kfm": "...", "scale": "1.0"}, "stat": {"hp": "3000", ...},
              "skills": [[50100850, 1], [50109901, 1]],
              "ai": {"melee": 1, "specials": [{"idx": 2, "cooltime": 7000, "initialCooltime": 3000}]}
                    | {"file": "<path to an AI xml>"},
              "server_set": {tag: {attr: v}}, "client_set": {tag: {attr: v}},
              "model_dir": "<tools/nif/mkmodel.py output folder>"}]   optional: a new model named model.kfm,
                    packed into Model/Npc.m2d <id[:2]>/<id>/ and Model/Textures.m2d npc_custom/ (areas npc-model,
                    textures), its anikeytext row into both Xml.m2d, asset-web urns cloned from the clone_from
                    model's (or "model_ref": <kfm name>)

Every motion a boss plays must name a sequence of its kfm, and each attack's point ("p0") must be a key of that
sequence in anikeytext.xml: the server fires an NPC attack only when the playing sequence reaches that key (the
build fails otherwise; tools/nif/mkmodel.py --key adds keys to a new model).

Skill attribute names: "motion" is <motionProperty> on the server and <motion> on the client, "range" is
<rangeProperty>/<range>, "damage" is <damageProperty>/<damage>, "attack" is <attack> on both.  Only attributes that
some shipped row of the same tag carries are accepted.  The client's npcdata/skilldata rows are compact (defaults
omitted); a set attribute is added to them when absent.
"""
import re
import uuid
from pathlib import Path

from newmap import SpecError, XText, esc, get_attr, set_attr  # noqa: E402

AI_DIR = "AI/Custom"


def _attrs(tag_text):
    return dict(re.findall(r'\s(\w+)="([^"]*)"', tag_text))


def _set_first(block, tag, attrs, where, allowed=None):
    """Set attrs on the first <tag ...> start tag of block (tag must exist)."""
    m = re.search(r"<" + re.escape(tag) + r"(\s[^>]*)?/?>", block)
    if not m:
        raise SpecError(f"{where}: no <{tag}> element in the cloned row (only attributes can be set)")
    st = m.group(0)
    for a, v in attrs.items():
        if allowed is not None and a not in allowed:
            raise SpecError(f"{where}: <{tag} {a}=...> is not used by any shipped row of this table")
        st = set_attr(st, a, v)
    return block[:m.start()] + st + block[m.end():]


def _set_all(block, tag, attrs, where, allowed=None):
    """Set attrs on every <tag ...> start tag of block (at least one must exist)."""
    if not re.search(r"<" + re.escape(tag) + r"[\s/>]", block):
        raise SpecError(f"{where}: no <{tag}> element in the cloned motion")
    for a in attrs:
        if allowed is not None and a not in allowed:
            raise SpecError(f"{where}: <{tag} {a}=...> is not used by any shipped row of this table")

    def fix(m):
        st = m.group(0)
        for a, v in attrs.items():
            st = set_attr(st, a, v)
        return st
    return re.sub(r"<" + re.escape(tag) + r"(\s[^>]*)?/?>", fix, block)


def _strip_comments(t):
    return re.sub(r"<!--.*?-->", "", t, flags=re.S)


class Universe:
    """attribute names per tag over a set of xml texts (lazy)."""

    def __init__(self, texts):
        self.texts, self.cache = texts, {}

    def __call__(self, tag):
        if tag not in self.cache:
            s = set()
            pat = re.compile(r"<" + re.escape(tag) + r"\s[^>]*>")
            for t in self.texts():
                for st in pat.findall(t):
                    s.update(re.findall(r'\s(\w+)="', st))
            self.cache[tag] = s
        return self.cache[tag]


SKILL_TAGS = {  # spec key -> (server tag, client tag)
    "motion": ("motionProperty", "motion"),
    "attack": ("attack", "attack"),
    "range": ("rangeProperty", "range"),
    "damage": ("damageProperty", "damage"),
    "detect": ("detectProperty", "detectProperty"),
}


class Bosses:
    def __init__(self, gen):
        self.g = gen
        self.spec = gen.spec
        self.files = {}  # (area, name) -> XText
        self.cskill_files = None

    # -- helpers
    def xt(self, area, arch, name):
        key = (area, name)
        if key not in self.files:
            f = arch.file(name)
            if f is None:
                raise SpecError(f"{arch.path.name}: no {name}")
            self.files[key] = XText(f)
        return self.files[key]

    def server_skill_name(self, sid):
        return f"skill/{str(sid)[:2]}/{sid}.xml"

    def server_npc_name(self, nid):
        s = f"{nid:08d}"
        return f"npc/{s[:2]}/{s[2:4]}/{s}.xml"

    def client_group(self, arch, folder, xid, tag):
        """client file holding rows of xid's id prefix (npcdata/230.xml for 23000039)."""
        s = str(xid)
        for k in (3, 4, 2):
            name = f"{folder}/{s[:k]}.xml"
            if arch.has(name):
                return name
        raise SpecError(f"client {folder}/: no file for the id prefix of {xid}")

    def client_row(self, text, tag, xid):
        m = re.search(r'[^\S\n]*<' + tag + r' id="' + str(xid) + r'"[^>]*>.*?</' + tag + r'>[^\S\n]*\r?\n?', text, re.S)
        return m

    def add_name(self, area, arch, file, xid, names, ref_id, langs=None):
        for n in arch.under("string/"):
            mm = re.fullmatch(r"string/([a-z]+)/" + file, n.lower())
            if not mm or (langs and mm.group(1) not in langs):
                continue
            lang = mm.group(1)
            name = names.get(lang) or names.get("en") or next(iter(names.values()))
            xt = self.xt(area, arch, n)
            if re.search(r'<key id="0*%d"' % xid, _strip_comments(xt.text)):
                raise SpecError(f"{area} {n}: id {xid} already has a name row")
            ms = xt.find_live(r'[^\S\n]*<key id="0*%d"[^>]*/>[^\S\n]*\r?\n' % ref_id)
            if ms:
                row = ms[0].group(0)
                at = ms[0]
            else:  # reference has no row in this language: clone the last row of the file
                ms = xt.find_live(r'[^\S\n]*<key id="[^"]*"[^>]*/>[^\S\n]*\r?\n')
                if not ms:
                    self.g.notes.append(f"{area} {n}: no <key> rows; {xid} not named")
                    continue
                row, at = ms[-1].group(0), ms[-1]
            key = re.search(r"<key [^>]*/>", row).group(0)
            new = set_attr(re.sub(r'(<key id=")[^"]*"', lambda q: q.group(1) + str(xid) + '"', key, count=1), "name", name)
            xt.insert_after(at, row.replace(key, new))

    # -- effects
    def effect(self, key, eff, sid):
        cx = self.g.cx
        f = eff.get("file") or f"custom/eff_{sid}_{key}.xml"
        f = f.lower()
        if f.startswith("effect/"):
            f = f[len("effect/"):]
        name = f"effect/{f}"
        if cx.has(name):
            raise SpecError(f"skill {sid} effect {key}: {name} already exists in the client Xml.m2d")
        parts = []
        for i, p in enumerate(eff.get("parts", [])):
            src = "effect/" + p["from"].replace("\\", "/").lower()
            sf = cx.file(src)
            if sf is None:
                raise SpecError(f"skill {sid} effect {key} part {i}: {src} is not in the client Xml.m2d")
            text = _strip_comments(XText(sf).text)
            els = re.findall(r"[^\S\n]*<effect\s[^>]*?(?:/>|>.*?</effect>)", text, re.S)
            if p.get("only"):
                els = [e for e in els if re.search(p["only"], e)]
            if not els:
                raise SpecError(f"skill {sid} effect {key} part {i}: no <effect> elements in {src}")
            for e in els:
                st = re.match(r"\s*<effect\s[^>]*?/?>", e).group(0)
                st2 = st
                if "delay" in p:
                    d = float(get_attr(st, "startDelay") or 0) + float(p["delay"])
                    st2 = set_attr(st2, "startDelay", f"{d:.2f}")
                if "duration" in p and get_attr(st, "duration") is not None:
                    st2 = set_attr(st2, "duration", f"{float(p['duration']):.2f}")
                elif "duration" in p and (get_attr(st, "loop") or "").lower() == "true":
                    # a looping part (a ground guide circle) shown for a fixed time instead
                    st2 = re.sub(r'\sloop="[^"]*"', f' duration="{float(p["duration"]):.2f}"', st2, count=1)
                e = e.replace(st, st2, 1)
                if "scale" in p:
                    def sc(m):
                        return m.group(1) + f"{float(m.group(2)) * float(p['scale']):.3f}" + m.group(3)
                    e = re.sub(r'(<transform\b[^>]*\sscale=")([-0-9.]+)(")', sc, e)
                if "translate" in p:
                    t = ", ".join(f"{float(v):.1f}" for v in p["translate"])
                    e = re.sub(r'(<transform\b[^>]*\stranslate=")[^"]*(")', lambda m: m.group(1) + t + m.group(2), e)
                parts.append(e.strip("\r\n"))
        if not parts:
            raise SpecError(f"skill {sid} effect {key}: no parts")
        body = '﻿<?xml version="1.0" encoding="utf-8"?>\n<ms2>\n' + "\n".join(
            "  " + p.strip() for p in parts) + "\n</ms2>\n"
        out = self.g.out / "client-xml" / name
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_bytes(body.encode("utf-8"))
        self.g.written.setdefault("client-xml", []).append(name)
        # the skill rows reference effects relative to effect/, in the shipped mixed case style
        return f

    # -- skills
    def skills(self):
        g = self.g
        sx, cx = g.sx, g.cx
        s_univ = Universe(lambda: (XText(sx.file(n)).text for n in self._sample_server_skills()))
        c_univ = Universe(lambda: (cx.fetch("skilldata/") or XText(cx.file(n)).text for n in cx.under("skilldata/")))
        for sk in self.spec.get("skills", []):
            sid = int(sk["id"])
            where = f"skills[{sid}]"
            sname = self.server_skill_name(sid)
            if sx.has(sname):
                raise SpecError(f"{where}: {sname} already exists in the server Xml.m2d")
            cname = self.client_group(cx, "skilldata", sid, "skill")
            cxt = self.xt("client-xml", cx, cname)
            if self.client_row(cxt.text, "skill", sid):
                raise SpecError(f"{where}: {cname} already has skill {sid}")
            effects = {k: self.effect(k, e, sid) for k, e in sk.get("effects", {}).items()}
            motions = sk.get("motions") or []
            if not motions:
                raise SpecError(f"{where}: no motions")
            s_motions, c_motions = [], []
            s_level0 = c_level0 = c_head = s_head = None
            for i, mo in enumerate(motions):
                fsid, flv = int(mo["from"][0]), int(mo["from"][1])
                fmi = int(mo["from"][2]) if len(mo["from"]) > 2 else 0
                mw = f"{where} motions[{i}]"
                # server
                sf = sx.file(self.server_skill_name(fsid))
                if sf is None:
                    raise SpecError(f"{mw}: skill {fsid} is not in the server Xml.m2d")
                st = XText(sf).text
                lv = re.search(r'[^\S\n]*<level value="%d">.*?</level>[^\S\n]*\r?\n' % flv, st, re.S)
                if not lv:
                    raise SpecError(f"{mw}: server skill {fsid} has no level {flv}")
                sm = re.findall(r"[^\S\n]*<motion>.*?</motion>[^\S\n]*\r?\n", lv.group(0), re.S)
                # client
                crow = self.client_row(self.xt("client-xml", cx, self.client_group(cx, "skilldata", fsid, "skill")).text,
                                       "skill", fsid)
                if not crow:
                    raise SpecError(f"{mw}: client skilldata has no skill {fsid}")
                clv = re.search(r'[^\S\n]*<level value="%d">.*?</level>[^\S\n]*\r?\n' % flv, crow.group(0), re.S)
                if not clv:
                    raise SpecError(f"{mw}: client skill {fsid} has no level {flv}")
                cm = re.findall(r"[^\S\n]*<motion\s[^>]*>.*?</motion>[^\S\n]*\r?\n", clv.group(0), re.S)
                if fmi >= len(sm) or fmi >= len(cm):
                    raise SpecError(f"{mw}: skill {fsid} level {flv} has {len(sm)} server / {len(cm)} client motions")
                s, c = sm[fmi], cm[fmi]
                for k, (stag, ctag) in SKILL_TAGS.items():
                    if k == "detect" or k not in mo:
                        continue
                    attrs = {a: str(v) for a, v in mo[k].items()}
                    s = _set_all(s, stag, attrs, f"{mw} {k} (server)", s_univ(stag))
                    c = _set_all(c, ctag, attrs, f"{mw} {k} (client)", c_univ(ctag))
                if mo.get("hit") is False:
                    # a wind-up / telegraph motion: no attacks, so nothing has to fire (or fails to)
                    s = re.sub(r"[^\S\n]*<attack\s[^>]*>.*?</attack>[^\S\n]*\r?\n", "", s, flags=re.S)
                    c = re.sub(r"[^\S\n]*<attack\s[^>]*>.*?</attack>[^\S\n]*\r?\n", "", c, flags=re.S)
                if mo.get("effect"):
                    if mo["effect"] not in effects:
                        raise SpecError(f"{mw}: effect {mo['effect']} is not in the skill's effects")
                    ef = effects[mo["effect"]]
                    s = _set_first(s, "motionProperty", {"motionEffect": ef}, mw)
                    c = _set_first(c, "motion", {"motionEffect": ef}, mw)
                s_motions.append(s)
                c_motions.append(c)
                if i == 0:
                    s_level0, c_level0 = lv.group(0), clv.group(0)
                    s_head, c_head = st, crow.group(0)
            # server file: the first source's file, its levels replaced by one level 1 with the collected motions
            lvl = re.sub(r'<level value="\d+">', '<level value="1">', s_level0, count=1)
            mspan = [m.span() for m in re.finditer(r"[^\S\n]*<motion>.*?</motion>[^\S\n]*\r?\n", lvl, re.S)]
            lvl = lvl[:mspan[0][0]] + "".join(s_motions) + lvl[mspan[-1][1]:]
            if "detect" in sk:
                lvl = _set_first(lvl, "detectProperty", {a: str(v) for a, v in sk["detect"].items()}, where,
                                 s_univ("detectProperty"))
            levels = list(re.finditer(r'[^\S\n]*<level value="\d+">.*?</level>[^\S\n]*\r?\n', s_head, re.S))
            stext = s_head[:levels[0].start()] + lvl + s_head[levels[-1].end():]
            out = g.out / "server-xml" / sname
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_bytes(b"\xef\xbb\xbf" + stext.encode("utf-8") if sf.read_bytes().startswith(b"\xef\xbb\xbf")
                            else stext.encode("utf-8"))
            g.written.setdefault("server-xml", []).append(sname)
            # client row
            clvl = re.sub(r'<level value="\d+">', '<level value="1">', c_level0, count=1)
            cspan = [m.span() for m in re.finditer(r"[^\S\n]*<motion\s[^>]*>.*?</motion>[^\S\n]*\r?\n", clvl, re.S)]
            clvl = clvl[:cspan[0][0]] + "".join(c_motions) + clvl[cspan[-1][1]:]
            if "detect" in sk:
                if re.search(r"<detectProperty\s", clvl):
                    clvl = _set_first(clvl, "detectProperty", {a: str(v) for a, v in sk["detect"].items()}, where)
                else:
                    attrs = " ".join(f'{a}="{esc(v)}"' for a, v in sk["detect"].items())
                    clvl = re.sub(r'(<level value="1">\r?\n)', lambda m: m.group(1) + f"\t\t\t<detectProperty {attrs} />\n",
                                  clvl, count=1)
            clevels = list(re.finditer(r'[^\S\n]*<level value="\d+">.*?</level>[^\S\n]*\r?\n', c_head, re.S))
            crow = c_head[:clevels[0].start()] + clvl + c_head[clevels[-1].end():]
            crow = re.sub(r'<skill id="\d+"', f'<skill id="{sid}"', crow, count=1)
            label = esc("custom " + str(sk.get("names", {}).get("en", sid)))
            crow = re.sub(r'(<skill id="\d+")[^>]*>', lambda m: m.group(1) + f' comment="{label}">', crow, count=1)
            last = list(re.finditer(r"</skill>[^\S\n]*\r?\n", cxt.text))[-1]
            cxt.insert_after(last, "\t" + crow.lstrip())
            names = sk.get("names") or {"en": f"Custom skill {sid}"}
            self.add_name("client-xml", cx, "skillname.xml", sid, names, int(motions[0]["from"][0]))
            self.add_name("server-xml", sx, "skillname.xml", sid, names, int(motions[0]["from"][0]), {"en"})

    def _sample_server_skills(self):
        """a few hundred shipped server skill files, enough for the attribute universe of each tag."""
        names = [n for n in self.g.sx.under("skill/") if n.lower().endswith(".xml")]
        names = names[::max(1, len(names) // 400)]
        self.g.sx.fetch("skill/")
        return names

    # -- bosses
    def ai_text(self, b, where):
        ai = b.get("ai") or {"melee": 1}
        if ai.get("file"):
            p = Path(ai["file"])
            if not p.is_file():
                raise SpecError(f"{where}: ai file {p} not found")
            return p.read_bytes()
        n = len(b["skills"])
        melee = int(ai.get("melee", 1))
        specials = ai.get("specials", [])
        for x in [melee] + [int(s["idx"]) for s in specials]:
            if not 1 <= x <= n:
                raise SpecError(f"{where}: ai skill idx {x} is outside the boss's {n} skills (1-based)")
        lines = ['<?xml version="1.0" encoding="utf-8"?>', "<npcAi>",
                 "\t<!-- generated by tools/dungeon/boss.py: specials when off cooldown, else melee in range, else trace -->",
                 "\t<battle>"]
        probs = [str(int(s.get("prob", 100))) for s in specials] + ["1"]
        lines.append(f'\t\t<node name="select" prob="{",".join(probs)}">')
        for s in specials:
            cd = int(s.get("cooltime", 8000))
            ini = int(s.get("initialCooltime", 3000))
            lines += [f'\t\t\t<node name="skill" idx="{int(s["idx"])}" faceTarget="1" cooltime="{cd}" initialCooltime="{ini}" isKeepBattle="true">',
                      f'\t\t\t\t<node name="standby" limit="{int(s.get("after", 1500))}" faceTarget="1" />',
                      "\t\t\t</node>"]
        lines += ['\t\t\t<node name="conditions">',
                  f'\t\t\t\t<condition name="skillRange" skillIdx="{melee}">',
                  f'\t\t\t\t\t<node name="skill" idx="{melee}" faceTarget="1">',
                  f'\t\t\t\t\t\t<node name="standby" limit="{int(ai.get("melee_after", 1000))}" faceTarget="1" />',
                  "\t\t\t\t\t</node>",
                  "\t\t\t\t</condition>",
                  '\t\t\t\t<condition name="true">',
                  f'\t\t\t\t\t<node name="trace" limit="2500" skillIdx="{melee}" />',
                  "\t\t\t\t</condition>",
                  "\t\t\t</node>",
                  "\t\t</node>",
                  "\t</battle>",
                  "</npcAi>", ""]
        return "\n".join(lines).encode("utf-8")

    def bosses(self):
        g = self.g
        sx, cx, sv = g.sx, g.cx, g.sv
        custom_skills = {int(s["id"]) for s in self.spec.get("skills", [])}
        for b in self.spec.get("bosses", []):
            nid, ref = int(b["id"]), int(b["clone_from"])
            where = f"bosses[{nid}]"
            sname, rname = self.server_npc_name(nid), self.server_npc_name(ref)
            if sx.has(sname):
                raise SpecError(f"{where}: {sname} already exists in the server Xml.m2d")
            rf = sx.file(rname)
            if rf is None:
                raise SpecError(f"{where}: clone_from {ref} has no {rname} in the server Xml.m2d")
            cname = self.client_group(cx, "npcdata", nid, "npc")
            cxt = self.xt("client-xml", cx, cname)
            if self.client_row(cxt.text, "npc", nid):
                raise SpecError(f"{where}: {cname} already has npc {nid}")
            rcx = self.xt("client-xml", cx, self.client_group(cx, "npcdata", ref, "npc"))
            crow_m = self.client_row(rcx.text, "npc", ref)
            if not crow_m:
                raise SpecError(f"{where}: clone_from {ref} has no client npcdata row")
            sxt = XText(rf)
            st, crow = sxt.text, crow_m.group(0)
            # skills
            sk = b.get("skills")
            if sk:
                for s_id, s_lv in sk:
                    if int(s_id) not in custom_skills and not sx.has(self.server_skill_name(int(s_id))):
                        raise SpecError(f"{where}: skill {s_id} is neither shipped nor in the spec's skills")
                ids = ",".join(str(int(x[0])) for x in sk)
                lvs = ",".join(str(int(x[1])) for x in sk)
                one = ",".join("1" for _ in sk)
                st = _set_first(st, "skill", {"ids": ids, "levels": lvs, "priorities": one,
                                              "probs": ",".join(str(max(1, 100 // len(sk))) for _ in sk)}, where)
                crow = _set_first(crow, "skill", {"ids": ids, "levels": lvs}, where)
            # model, level, stats
            if "model_dir" in b:
                self.model(b, nid, where)
            if "model" in b:
                m = {k: str(v) for k, v in b["model"].items()}
                st = _set_first(st, "model", m, where)
                crow = _set_first(crow, "model", m, where)
            if "level" in b:
                lv = str(int(b["level"]))
                st = _set_first(st, "basic", {"level": lv, "difficulty": lv}, where)
                crow = _set_first(crow, "environment", {"level": lv}, where)
            if "stat" in b:
                s = {k: str(v) for k, v in b["stat"].items()}
                st = _set_first(st, "stat", s, where)
                crow = _set_first(crow, "stat", s, where)
            if "exp" in b:
                st = _set_first(st, "exp", {"customExp": str(b["exp"])}, where)
                crow = _set_first(crow, "environment", {"customExp": str(b["exp"])}, where)
            for tag, attrs in b.get("server_set", {}).items():
                st = _set_first(st, tag, {k: str(v) for k, v in attrs.items()}, where)
            for tag, attrs in b.get("client_set", {}).items():
                crow = _set_first(crow, tag, {k: str(v) for k, v in attrs.items()}, where)
            self.check_sequences(crow, where, {int(x[0]) for x in sk or []})
            # AI
            ai_path = f"{AI_DIR}/AI_{nid}.xml"
            if sv.has(ai_path):
                raise SpecError(f"{where}: {ai_path} already exists in Server.m2d")
            data = self.ai_text(b, where)
            out = g.out / "server" / ai_path
            out.parent.mkdir(parents=True, exist_ok=True)
            out.write_bytes(data)
            g.written.setdefault("server", []).append(ai_path)
            rel = ai_path[len("AI/"):]
            st = _set_first(st, "aiInfo", {"path": rel}, where)
            if re.search(r"<aiInfo\s", crow):
                crow = _set_first(crow, "aiInfo", {"path": rel}, where)
            # write
            out = g.out / "server-xml" / sname
            out.parent.mkdir(parents=True, exist_ok=True)
            sxt.text = st
            sxt.write(out)
            g.written.setdefault("server-xml", []).append(sname)
            crow = re.sub(r'<npc id="\d+"', f'<npc id="{nid}"', crow, count=1)
            last = list(re.finditer(r"</npc>[^\S\n]*\r?\n", cxt.text))[-1]
            cxt.insert_after(last, "\t" + crow.lstrip())
            names = b.get("names") or {"en": f"Custom boss {nid}"}
            self.add_name("client-xml", cx, "npcname.xml", nid, names, ref)
            self.add_name("server-xml", sx, "npcname.xml", nid, names, ref, {"en"})

    def check_sequences(self, crow, where, listed):
        """Every motion of every skill the boss uses must name a sequence of the boss's kfm (else the client plays
        no animation for it), and that sequence must carry a key for each of the motion's <attack point=...> (the
        server fires an attack when the playing sequence reaches its key; without one the attack never hits).
        Sequences and keys come from the kfm's anikeytext.xml row (a model_dir's row has been merged in by model()
        already; see tools/nif/mkmodel.py --key); the skill rows include the spec's custom ones.  A missing
        sequence is an error; a missing hit key is an error for the skills the spec lists and a note for the ones
        inherited from clone_from (shipped NPCs have wind-up motions whose attacks never fire)."""
        cx = self.g.cx
        mt = re.search(r"<model\s[^>]*>", crow)
        kfm = get_attr(mt.group(0), "kfm") if mt else None
        if not kfm:
            raise SpecError(f"{where}: the npc row has no model kfm")
        ani = self.xt("client-xml", cx, "anikeytext.xml").text
        row = re.search(r'<kfm name="%s">(.*?)</kfm>' % re.escape(kfm.lower()), ani, re.S | re.I)
        if not row:
            raise SpecError(f"{where}: anikeytext.xml has no row for kfm {kfm}, so its sequences can't be checked")
        seqs = {m.group(1).lower(): set(re.findall(r'<key name="([^"]*)"', m.group(2)))
                for m in re.finditer(r'<seq\s[^>]*\bname="([^"]*)"[^>]*>(.*?)</seq>', row.group(1), re.S)}
        st = re.search(r"<skill\s[^>]*>", crow)
        ids = (get_attr(st.group(0), "ids") or "") if st else ""
        lvs = (get_attr(st.group(0), "levels") or "") if st else ""
        bad, quiet = [], []
        for sid, lv in zip(ids.split(","), lvs.split(",")):
            if not sid.strip():
                continue
            sid, lv = int(sid), int(lv or 1)
            srow = self.client_row(self.xt("client-xml", cx, self.client_group(cx, "skilldata", sid, "skill")).text,
                                   "skill", sid)
            if not srow:
                bad.append(f"skill {sid}: no client skilldata row")
                continue
            lrow = re.search(r'<level value="%d">.*?</level>' % lv, srow.group(0), re.S)
            if not lrow:
                bad.append(f"skill {sid}: no level {lv}")
                continue
            for i, m in enumerate(re.findall(r"<motion\s[^>]*>.*?</motion>", lrow.group(0), re.S)):
                seq = get_attr(re.match(r"<motion\s[^>]*>", m).group(0), "sequenceName")
                if not seq:
                    continue
                if seq.lower() not in seqs:
                    bad.append(f"skill {sid} level {lv} motion {i}: sequenceName {seq}")
                    continue
                for pt in re.findall(r'<attack\s[^>]*\bpoint="([^"]+)"', m):
                    if pt not in seqs[seq.lower()]:
                        (bad if sid in listed else quiet).append(
                            f"skill {sid} level {lv} motion {i}: attack point {pt} is no key of {seq} "
                            f"(keys: {', '.join(sorted(seqs[seq.lower()]))})")
        if quiet:
            self.g.notes.append(f"{where}: inherited skill motions that never hit: " + "; ".join(sorted(set(quiet))))
        if bad:
            raise SpecError(f"{where}: kfm {kfm} lacks the animations or hit keys its skills play (mkmodel --map / "
                            f"--key, or motions[].motion.sequenceName):\n  " + "\n  ".join(bad)
                            + f"\n  {kfm} has: {', '.join(sorted(seqs))}")

    # -- a new NPC model (tools/nif/mkmodel.py folder)
    MODEL_REF = {".kfm": "kfm", ".nif": "nif", ".kf": "kf", ".dds": "dds"}

    def model(self, b, nid, where):
        """Pack b["model_dir"] (tools/nif/mkmodel.py output) as a new model: <kfm>.kfm/.nif/.xml and the .kf files
        go to Model/Npc.m2d <id[:2]>/<id>/ (lower case entry names), the .dds files to Model/Textures.m2d
        npc_custom/, the anikeytext.xml row into both Xml.m2d anikeytext.xml, and one asset-web urn per file
        (triples cloned from the reference model's kfm / nif / kf and a shipped texture, new uuid5 urn, new
        path and name)."""
        g = self.g
        d = Path(b["model_dir"])
        kfm = str((b.get("model") or {}).get("kfm", ""))
        if not kfm or not (d / f"{kfm}.kfm").is_file():
            raise SpecError(f'{where}: model_dir {d} has no "{kfm}.kfm" (model.kfm names the model)')
        folder = f"{str(nid)[:2]}/{nid}"
        npc_arch = g.archive("npc-model", g.res / "Model/Npc.m2d")
        if npc_arch.under(folder + "/"):
            raise SpecError(f"{where}: Npc.m2d already has {folder}/")
        tex_arch = None
        aw = []  # (kind, relpath, name)
        for f in sorted(d.iterdir()):
            ext = f.suffix.lower()
            if ext in (".kfm", ".nif", ".kf", ".xml") and f.name != "anikeytext.xml":
                g.emit("npc-model", f"{folder}/{f.name.lower()}", f)
                if ext != ".xml":
                    aw.append((ext, f"/Model/Npc/{folder}/{f.name}", f.stem))
            elif ext == ".dds":
                tex_arch = tex_arch or g.archive("textures", g.res / "Model/Textures.m2d")
                if tex_arch.has(f"npc_custom/{f.name.lower()}"):
                    raise SpecError(f"{where}: Textures.m2d already has npc_custom/{f.name}")
                g.emit("textures", f"npc_custom/{f.name.lower()}", f)
                aw.append((ext, f"/Model/Textures/Npc_Custom/{f.name}", f.stem))
        # anikeytext: the client and server both key sequence times by the lower-case kfm name
        ani = d / "anikeytext.xml"
        if not ani.is_file():
            raise SpecError(f"{where}: {d} has no anikeytext.xml")
        row = ani.read_text(encoding="utf-8")
        for area, arch in (("client-xml", g.cx), ("server-xml", g.sx)):
            xt = self.xt(area, arch, "anikeytext.xml")
            if re.search(r'<kfm name="%s">' % re.escape(kfm.lower()), xt.text, re.I):
                raise SpecError(f"{where}: anikeytext.xml already has {kfm.lower()}")
            last = list(re.finditer(r"</kfm>[^\S\n]*\r?\n", xt.text))[-1]
            xt.insert_after(last, row)
        # asset web
        rel = g.aw.file("relpath.nt").read_bytes()
        ref = str(b.get("model_ref") or "")
        cref = (self.xt("client-xml", g.cx, self.client_group(g.cx, "npcdata", int(b["clone_from"]), "npc"))
                .text)
        m = re.search(r'<npc id="%d".*?<model [^>]*kfm="([^"]+)"' % int(b["clone_from"]), cref, re.S)
        ref_kfm = ref or (m.group(1) if m else "")

        def urn_of(rx):
            m = re.search(rb'^<(urn:uuid:[^>]+)> <[^>]*/relpath> "(%s)"\.$' % rx, rel, re.M | re.I)
            return (m.group(1), m.group(2).decode()) if m else (None, None)
        k_urn, k_rel = urn_of(rb"/Model/Npc/[^\"]*/" + re.escape(ref_kfm.encode()) + rb"\.kfm")
        if not k_urn:
            raise SpecError(f"{where}: asset-web-metadata.m2d has no relpath row for {ref_kfm}.kfm")
        rdir = k_rel.rsplit("/", 1)[0]
        refs = {".kfm": (k_urn, k_rel),
                ".nif": urn_of(re.escape(rdir.encode()) + rb"/[^/\"]*\.nif"),
                ".kf": urn_of(re.escape(rdir.encode()) + rb"/Idle_A\.kf"),
                ".dds": urn_of(rb"/Model/Textures/Item_Body/F_PA\.dds")}
        for ext, (u, _) in refs.items():
            if u is None and any(a[0] == ext for a in aw):
                raise SpecError(f"{where}: no reference asset-web urn for {ext}")
        nts = {n: g.aw.file(n).read_bytes() for n in g.aw.names if n.lower().endswith(".nt")}
        extra = g.extra_awm
        for ext, relpath, name in aw:
            ru, rrel = refs[ext]
            new_urn = f"urn:uuid:{uuid.uuid5(uuid.NAMESPACE_URL, 'ms2-custom-model:' + relpath.lower())}"
            vals = {"relpath": relpath, "logpath": relpath.lower(), "canonical": relpath.rsplit("/", 1)[0],
                    "name": name}
            for n, data in nts.items():
                for l in data.split(b"\n"):
                    if not l.startswith(b"<" + ru + b">"):
                        continue
                    l = l.decode().replace(ru.decode(), new_urn, 1)
                    pm = re.match(r'(<[^>]+> <[^>]*/(\w+)> ")[^"]*("\.)$', l)
                    if pm and pm.group(2) in vals:
                        l = pm.group(1) + vals[pm.group(2)] + pm.group(3)
                    extra.setdefault(n, []).append(l)
        g.notes.append(f"bosses[{nid}]: model {kfm} -> Npc.m2d {folder}/ ({len(aw)} asset-web urns)")

    def run(self):
        if not (self.spec.get("bosses") or self.spec.get("skills")):
            return
        self.skills()
        self.bosses()
        for (area, name), xt in self.files.items():
            if name.startswith(("npcdata/", "skilldata/", "string/")) or name == "anikeytext.xml":
                self.g.emit(area, name, xt)
        n = len(self.spec.get("bosses", []))
        self.g.notes.append(f"bosses: {n} boss(es), {len(self.spec.get('skills', []))} custom skill(s)")


def custom_npc_ids(spec):
    return {int(b["id"]) for b in spec.get("bosses", [])}
