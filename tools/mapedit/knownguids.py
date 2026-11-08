#!/usr/bin/env python3
"""Build known_guids.json: real model GUIDs that can be recovered for MS2's library models.

Sources (all read-only):
  * MS2 Library.m2d   *.diagram  -> EntityModelID list of the real model GUIDs of that library
                      *.preset   -> Mixin SourceID/SourceName = real GUID of the base model
  * Gamebryo SDK Media/*ModelLibrary/*.model  (name -> ID): MS2's standard/physx models are the SDK ones, accepted only
    when the SDK GUID occurs in an MS2 diagram (so it is certain that MS2 uses that GUID for that model).
  * the whole SDK tree (MS2_GAMEBRYO_APPDIR, e.g. Samples/GameDemos/MangledMetal/Data/TriggersLibrary/EntityModels,
    which is the TriggersLibrary MS2 shipped): same rule, GUID occurs in an MS2 diagram  -> certain
  * elimination: a library whose diagram has exactly one still-unmatched GUID and whose project has exactly one
    still-unmatched model (GIModelLibrary: GIScene)                                    -> near certain
  * LAYOUT_GUESSES: the 15 BeastModelLibrary models exist nowhere else (no SDK copy).  The diagram stores only GUID +
    position + ZIndex (items sorted by GUID, no names).  Clusters in the layout (mixers sit left of their mixins; ZIndex
    = order they were dropped on the diagram, mixins before the mixer) identify the groups; the order inside a group is
    assigned from the ZIndex order matching the flat's mixin order.  These are marked 'layout-guess' and are only good
    for making the diagram resolve; nothing else depends on them.
Every other model/preset gets a deterministic uuid5 (flatrecon.model_guid).

  knownguids.py [--library out/maps/extracted/Library] [--sdk <APPDIR>] [-o tools/mapedit/known_guids.json]
"""
import argparse, glob, json, os, re, sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))
import modtools_config as cfg  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))

ELIMINATION = [('gimodellibrary', 'gimodellibrary.diagram', 'GIScene')]

# BeastModelLibrary: name -> diagram GUID, from the diagram layout (see module docstring).  Groups:
#   mesh      : BeastMesh (left) + its mixins BeastMeshTextureBakeSettings, BeastMeshVertexBakeSettings, BeastRenderStats,
#               BeastBakeType in the column x~5 (ZIndex 20..23 = mixin order of beastmesh.flat); BeastMesh ZIndex 24
#   quality   : BeastQualitySettings (known) + 6 mixins in ZIndex 12,13,14,15,17,18 (= mixin order of
#               beastqualitysettings.flat) and 2 standalone settings (ZIndex 9,10: BeastGlobalSettings, BeastEnvironmentSettings)
#   lights    : BeastLight (ZIndex 8, between the Beast*Light models and the settings), BeastAreaLight (ZIndex 0)
LAYOUT_GUESSES = {
    'BeastMesh': 'e92b8d36-a539-42a6-8057-e1353d71f8f2',
    'BeastMeshTextureBakeSettings': 'a5dadebf-0fa2-45c7-bea6-8327df6974ec',
    'BeastMeshVertexBakeSettings': '7282eb2e-9665-44e2-8726-8fa652ca54fb',
    'BeastRenderStats': '264af924-c31d-4534-b2f7-14dc54b1cf07',
    'BeastBakeType': 'a15b9165-79ed-4a3b-9999-b268f239d5fb',
    'BeastIntegratorSettings': '981329d9-9ed4-4a56-b094-d3071bb5629c',
    'BeastFinalGatherSettings': '97279330-771f-46cf-ad83-e9d636797843',
    'BeastPathTracerSettings': '4bb0f049-b5c2-4909-ad6f-755e3a946c30',
    'BeastTextureBakeSettings': '7c37ccd2-0cca-4a54-9d0b-8712fb6d5ade',
    'BeastVertexBakeSettings': '58bbc463-9d14-4312-806e-16559dbf151a',
    'BeastBakePreferences': '8e2b9d07-3dbc-47d0-91ba-dba6e760f825',
    'BeastGlobalSettings': '0e573ef5-018a-4656-bf9c-53f034cf6150',
    'BeastEnvironmentSettings': 'b9f2f174-8ec0-4fca-8195-ec9ba97f11b9',
    'BeastLight': '6d9077f6-d325-41c6-9955-b249127a86e5',
    'BeastAreaLight': 'cb8951cf-371c-468d-9d09-b23c2c06b611',
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--library', default='out/maps/extracted/Library')
    ap.add_argument('--sdk', help='Gamebryo SDK APPDIR (default: MS2_GAMEBRYO_APPDIR)')
    ap.add_argument('-o', default=os.path.join(HERE, 'known_guids.json'))
    a = ap.parse_args()
    a.sdk = a.sdk or str(cfg.require('MS2_GAMEBRYO_APPDIR', 'knownguids.py'))
    diagram_ids = set()
    for f in glob.glob(os.path.join(a.library, '*', '*.diagram')) + glob.glob(os.path.join(a.library, '*', '*', '*.diagram')):
        diagram_ids.update(re.findall(r'EntityModelID="([0-9a-f-]{36})"', open(f, encoding='utf-8-sig').read()))
    known = {}
    src = {}
    for f in glob.glob(os.path.join(a.library, '*', '*.preset')):
        t = open(f, encoding='utf-8-sig').read()
        m = re.search(r'SourceID="([^"]+)"\s+SourceName="([^"]+)"', t)
        if m:
            known[m.group(2)] = m.group(1)
            src[m.group(2)] = 'ms2-preset-mixin'
    # the shipped library presets' own IDs (only those a project lists: Beast also ships stale copies under old names)
    for ep in glob.glob(os.path.join(a.library, '*', '*.emtproj')):
        txt = open(ep, encoding='utf-8-sig').read()
        for rel in re.findall(r'<File\s+path="([^"]+\.preset)"', txt):
            fp = os.path.join(os.path.dirname(ep), rel.replace('\\', '/'))
            if not os.path.exists(fp):
                fp = os.path.join(os.path.dirname(ep), rel.replace('\\', '/').lower())
            if os.path.exists(fp):
                m = re.search(r'\bID="([^"]+)"', open(fp, encoding='utf-8-sig').read(1500))
                nm = re.sub(r'\.preset$', '', rel.replace('\\', '/').split('/')[-1])
                if m:
                    known[nm] = m.group(1)
                    src[nm] = 'ms2-shipped-preset-own-id'
    sdk_names = {}
    for dp, dn, fn in os.walk(a.sdk):
        for f in fn:
            if f.endswith('.model'):
                p = os.path.join(dp, f)
                t = open(p, encoding='utf-8-sig', errors='replace').read(3000)
                m = re.search(r'\bID="([^"]+)"', t)
                if m and m.group(1) in diagram_ids:
                    sdk_names.setdefault(f[:-6], set()).add(m.group(1))
    for n, gs in sorted(sdk_names.items()):
        if len(gs) == 1 and n not in known:
            known[n] = next(iter(gs))
            src[n] = 'sdk-guid-confirmed-by-ms2-diagram'
    # elimination (single model left in a library diagram)
    for libdir, diagram, model in ELIMINATION:
        t = open(os.path.join(a.library, libdir, diagram), encoding='utf-8-sig').read()
        left = [g for g in re.findall(r'EntityModelID="([0-9a-f-]{36})"', t) if g not in known.values()]
        if len(left) == 1 and model not in known:
            known[model] = left[0]
            src[model] = 'elimination (only unmatched model of the library diagram)'
    for n, g in LAYOUT_GUESSES.items():
        if g in diagram_ids and n not in known and g not in known.values():
            known[n] = g
            src[n] = 'layout-guess'
    matched = set(known.values())
    out = dict(_comment='Real GUIDs recovered for MS2 library models; everything else is uuid5(NAMESPACE_URL, "ms2-model:"+name).',
               models=dict(sorted(known.items())), sources={k: src[k] for k in sorted(src)},
               diagram_guids_total=len(diagram_ids), diagram_guids_unmatched=sorted(diagram_ids - matched))
    json.dump(out, open(a.o, 'w'), indent=1)
    print('known guids:', len(known), 'of', len(diagram_ids), 'diagram GUIDs; unmatched', len(diagram_ids - matched))


if __name__ == '__main__':
    main()
