#!/usr/bin/env python3
"""Move one entity of a Toolbench .block (text edit, keeps the file's formatting).

  move_entity.py in.block -o out.block --id <entity guid, dashed> --position "x, y, z"

Replaces the value of the entity's Position property (error if it has none). Used by tools/mapedit/maptest.sh
for the edited-map client test (tools/mapedit/README.md).
"""
import argparse, re, sys

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('block'); ap.add_argument('-o', '--out', required=True)
    ap.add_argument('--id', required=True); ap.add_argument('--position', required=True)
    a = ap.parse_args()
    s = open(a.block, encoding='utf-8').read()
    m = re.search(r'<entity\s+id="%s".*?</entity>' % re.escape(a.id), s, re.S)
    if not m: sys.exit('entity %s not found' % a.id)
    ent = m.group(0)
    new, n = re.subn(r'(name="Position">\s*<set\s+value=")[^"]*(")', r'\g<1>%s\g<2>' % a.position, ent, count=1)
    if n != 1: sys.exit('entity has no Position property')
    open(a.out, 'w', encoding='utf-8', newline='').write(s[:m.start()] + new + s[m.end():])
    print('moved %s: %s -> %s' % (a.id, re.search(r'name="Position">\s*<set\s+value="([^"]*)"', ent).group(1), a.position))

main()
