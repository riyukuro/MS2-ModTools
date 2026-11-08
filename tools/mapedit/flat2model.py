#!/usr/bin/env python3
"""Regenerate Toolbench entity-modeling sources (.model / .preset / preset .emtproj) from MS2's exported .flat files.

  flat2model.py --root out/maps/toolbench_root [--exported out/maps/extracted/Exported]
                [--library out/maps/extracted/Library] [-j 3] [--only-library]

Writes into <root>/Library (created from --library if missing; <root>/Exported is symlinked to --exported):
  <root>/Library/<lib>/<Name>.model        for each <File path="*.model"> of the 7 shipped library .emtproj (and any
                                           library .flat the project forgot to list: the entry is added to the project copy)
  <root>/Library/Presets/Presets_<Cat>.emtproj + Presets/<Cat>/<Name>.preset   for the 13,127 per-object presets
Existing .preset files shipped in Library.m2d (tool, beast) are kept as they are.

See msmap/flatrecon.py for the inversion rules and validate_models.py for the validation.
GUIDs: tools/mapedit/known_guids.json (recovered ones) else uuid5 -- the same function xblock2block uses for model-id.
"""
import argparse, collections, json, os, re, shutil, sys, time
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from msmap import flatrecon as R
from msmap import model as M
from msmap.xmlio import NetWriter
from msmap.convert import LIB_EMTPROJ, cat_label

HERE = os.path.dirname(os.path.abspath(__file__))
SETTING = 'Emergent.Toolbench.EntityModeling.'
LIB_FLAT_DIR = {  # library dir -> FlatModelsOutput leaf used by MS2's own projects (informational for new projects)
    'maplestory2library': 'MapleStory2Library', 'standardmodellibrary': 'StandardModelLibrary',
    'physxmodellibrary': 'PhysXModelLibrary', 'triggerslibrary': 'TriggersLibrary',
    'beastmodellibrary': 'BeastModelLibrary', 'gimodellibrary': 'GIModelLibrary', 'tool': 'Tool'}


def load_known():
    p = os.path.join(HERE, 'known_guids.json')
    if os.path.exists(p):
        return json.load(open(p)).get('models', {})
    return {}


class GuidMap:
    """name -> model GUID (mapping-like, for xblock2block's model_ids)."""

    def __init__(self, known=None):
        self.known = load_known() if known is None else known
        self.cache = {}

    def get(self, name, default=None):
        g = self.cache.get(name)
        if g is None:
            g = self.cache[name] = R.model_guid(name, self.known)
        return g

    __getitem__ = get


def emtproj_text(files, refs, flat_out, models_out=''):
    out = []
    w = NetWriter(out, indent='  ', newline_attrs=True)
    w.start('Project', [('version', '5')])
    w.start('tool-settings', [('version', '1')])
    for k, v in (('BehaviorScriptsOutput', ''), ('DiagramsOutput', ''), ('EnumsOutput', '..\\..\\Enums'),
                 ('FlatModelsOutput', flat_out), ('ModelsAndPresetsOutput', models_out)):
        w.start('setting', [('id', SETTING + k), ('property-type', 'String')])
        w.leaf('value', v)
        w.end()
    w.end()
    w.start('Files')
    for f in files:
        w.empty('File', [('path', f)])
    w.end()
    w.start('References')
    for r in refs:
        w.empty('Project', [('path', r)])
    w.end()
    w.empty('virtual-folders', [('version', '1')])
    w.end()
    return w.finish()


def add_file_entry(text, rel):
    """Append <File path=rel/> to an existing emtproj text, matching its indentation style."""
    nl = '\r\n' if '\r\n' in text else '\n'
    m = re.search(r'^([ \t]*)</Files>', text, re.M)
    ind = m.group(1)
    if re.search(r'<File path="', text):          # tab style: one-line
        mi = re.search(r'^([ \t]*)<File path="', text, re.M)
        entry = '%s<File path="%s" />%s' % (mi.group(1), rel, nl)
    else:                                          # spaced multi-line style
        mi = re.search(r'^([ \t]*)<File\s*$', text, re.M)
        i1 = mi.group(1) if mi else ind + '  '
        entry = '%s<File%s%s  path="%s" />%s' % (i1, nl, i1, rel, nl)
    return text[:m.start()] + entry + text[m.start():]


def ensure_root(root, extracted):
    ex = os.path.abspath(extracted)
    os.makedirs(root, exist_ok=True)
    lib = os.path.join(root, 'Library')
    if not os.path.exists(lib):
        shutil.copytree(os.path.join(ex, 'Library'), lib)
    exp = os.path.join(root, 'Exported')
    if not os.path.lexists(exp):
        os.symlink(os.path.join(ex, 'Exported'), exp)


def ci_path(path):
    """Resolve `path` against the real filesystem ignoring case of existing components (Library.m2d names are lowercase)."""
    path = os.path.abspath(path)
    parts = path.split(os.sep)
    cur = os.sep
    for i, comp in enumerate(parts[1:], 1):
        nxt = os.path.join(cur, comp)
        if not os.path.exists(nxt) and os.path.isdir(cur):
            for e in os.listdir(cur):
                if e.lower() == comp.lower():
                    nxt = os.path.join(cur, e)
                    break
        cur = nxt
    return cur


def write_model(m, path):
    path = ci_path(path)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'wb') as f:
        f.write(M.model_text(m).encode('utf-8'))


def build(root, extracted='out/maps/extracted', jobs=3, log=print, only_library=False):
    ensure_root(root, extracted)
    ex = os.path.abspath(extracted)
    t0 = time.time()
    flats = R.load_flats(os.path.join(ex, 'Exported', 'flat'), jobs=jobs)
    log('flats: %d (%.1fs)' % (len(flats), time.time() - t0))
    known = load_known()
    rec = R.Recon(flats, known)
    lib = os.path.join(root, 'Library')
    stat = collections.Counter()

    # ---- library projects ------------------------------------------------------------------------------------
    lib_of_name = {}
    for libdir, rel in LIB_EMTPROJ.items():
        ep = os.path.join(lib, rel)
        if not os.path.exists(ep):
            continue
        text = open(ep, encoding='utf-8-sig').read()
        listed = re.findall(r'<File\s+path="([^"]+\.(?:model|preset))"', text)
        listed_names = {}
        for rel_f in listed:
            nm = re.sub(r'\.(model|preset)$', '', rel_f.replace('\\', '/').split('/')[-1])
            listed_names[nm.lower()] = (nm, rel_f)
        by_lower = {n.lower(): n for n, f in flats.items() if f.cat == ('library', libdir)}
        changed = False
        for lname, (nm, rel_f) in sorted(listed_names.items()):
            fname = by_lower.get(lname)
            lib_of_name[fname or nm] = libdir
            if fname is None:
                stat['listed-without-flat'] += 1
                continue
            target = os.path.join(os.path.dirname(ep), rel_f.replace('\\', '/'))
            if rel_f.lower().endswith('.preset'):
                if os.path.exists(ci_path(target)):
                    stat['library-preset-kept(shipped)'] += 1
                    continue
                write_model(rec.build_preset(fname), target)
                stat['library-preset-generated'] += 1
            else:
                write_model(rec.build(fname), target)
                stat['library-model-generated'] += 1
        for lname, fname in sorted(by_lower.items()):
            lib_of_name[fname] = libdir
            if lname not in listed_names:
                rel_f = fname + '.model'
                write_model(rec.build(fname), os.path.join(os.path.dirname(ep), rel_f))
                text = add_file_entry(text, rel_f)
                changed = True
                stat['library-model-generated-and-added-to-project'] += 1
                log('  %s: added %s to %s' % (libdir, rel_f, os.path.basename(ep)))
        if changed:
            open(ep, 'w', encoding='utf-8-sig', newline='').write(text)
    if only_library:
        return stat, rec

    # ---- preset projects -------------------------------------------------------------------------------------
    cats = collections.defaultdict(list)
    for n, f in flats.items():
        if f.cat[0] == 'presets':
            cats[f.cat[1]].append(n)
    pdir = os.path.join(lib, 'Presets')
    preset_label = {n: cat_label(('presets', f.cat[1])) for n, f in flats.items() if f.cat[0] == 'presets'}
    for cat, names in sorted(cats.items()):
        label = cat_label(('presets', cat))
        sub = label.split('_', 1)[-1] if '_' in label else label
        files, refs = [], set()
        for n in sorted(names, key=str.lower):
            m = rec.build_preset(n)
            write_model(m, os.path.join(pdir, sub, n + '.preset'))
            files.append(sub + '\\' + n + '.preset')
            base = m.mixins[0][1] if m.mixins else None
            ld = lib_of_name.get(base)
            if base in preset_label:       # preset of a preset: reference the project holding the base preset
                if preset_label[base] != label:
                    refs.add(preset_label[base] + '.emtproj')
            elif ld:
                refs.add('..\\' + os.path.dirname(LIB_EMTPROJ[ld]).replace('/', '\\') + '\\' +
                         os.path.basename(LIB_EMTPROJ[ld]))
            else:
                stat['preset-base-without-library'] += 1
            stat['preset-generated'] += 1
        flat_out = '..\\..\\Exported\\Flat\\Presets\\' + re.sub(r'(^| )(\w)', lambda mm: mm.group(1) + mm.group(2).upper(), cat)
        txt = emtproj_text(files, sorted(refs), flat_out, sub)
        with open(os.path.join(pdir, label + '.emtproj'), 'wb') as fh:
            fh.write(txt.encode('utf-8'))
        stat['preset-projects'] += 1
        log('  %s: %d presets, %d references' % (label, len(names), len(refs)))
    if rec.warn:
        stat['warnings'] = len(rec.warn)
        os.makedirs(os.path.join(root, 'logs'), exist_ok=True)
        open(os.path.join(root, 'logs', 'flat2model-warnings.txt'), 'w').write('\n'.join(rec.warn) + '\n')
    log('done in %.1fs' % (time.time() - t0))
    return stat, rec


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--root', default='out/maps/toolbench_root')
    ap.add_argument('--extracted', default='out/maps/extracted')
    ap.add_argument('-j', '--jobs', type=int, default=3)
    ap.add_argument('--only-library', action='store_true')
    a = ap.parse_args()
    stat, rec = build(os.path.abspath(a.root), a.extracted, a.jobs, only_library=a.only_library)
    print(json.dumps(dict(stat), indent=1))
    print('warnings:', len(rec.warn))
    for w in rec.warn[:10]:
        print('  ', w)


if __name__ == '__main__':
    main()
