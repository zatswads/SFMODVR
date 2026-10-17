"""Joint correctives of a DAZ figure (Genesis 9 body_cbs_*, Genesis 3 / 8 pJCM*) for SF Body Morphs' avatar mode.

daz_export.dsa (with "morphs") exports the correctives as blend shapes but not what drives them. DAZ keeps the drivers as formulas
(ERC) in each morph's .dsf: value = f(a bone's rotation channel), a product of helper morphs, a TCB spline over angle keys, times the
figure's master dial (body_basejointcorrectives). This tool reads those formulas and the figure's skeleton (rotation order, joint
orientation, joint centres) from the DAZ content libraries and writes them as a small text table the plugin evaluates every frame
(Avatar.cs, AvatarJcm):

    # comments
    figure <figure id> <figure .dsf>
    bone   <id> <parent> <rotation order> <centre x y z, cm> <orientation x y z, deg> <limits x min max, y, z, deg>
    morph  <key> <default> <min> <max> <clamped 0|1> <blend shape name or ->           (key = scope:id)
    f      <key> <sum|mult> <RPN tokens ...>

RPN tokens: r:<bone>:<x|y|z> (the bone's rotation channel, degrees), m:<key> (another morph's value), n:<number>,
k:<x>,<y> (a spline key), * + - / (binary ops), tcb / lin / const (a spline: pops the key count, the keys and the input).
A morph's value = (sum of its 'sum' formulas, or its default when it has none) x each 'mult' formula, clamped to [min, max] when
clamped. Rotation order: the first axis is applied first (checked against DAZ Studio's own world rotations: exact, except where a
channel's limit clamps it, which is why the limits are written too). The figure's own joint centres are those of the base figure (a character shape moves them a little; the plugin fits the
DAZ -> Unity frame over all joints, so a few mm do not matter).

Usage: python daz_correctives.py <morphs.txt from daz_export.dsa> <out correctives.txt> [--lib <content library>]...
(libraries default to DAZ Studio's content directories from the registry).
"""
import argparse
import gzip
import json
import os
import re
import sys
import urllib.parse


def libraries():
    libs = []
    try:
        import winreg
        for ver in ('Studio6', 'Studio4'):
            try:
                k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r'Software\DAZ\%s' % ver)
            except OSError:
                continue
            n = 0
            try:
                n = int(winreg.QueryValueEx(k, 'NumContentDirs')[0])
            except OSError:
                pass
            for i in range(n):
                try:
                    libs.append(winreg.QueryValueEx(k, 'ContentDir%d' % i)[0])
                except OSError:
                    pass
            if libs:
                break
    except ImportError:
        pass
    return [l for l in libs if os.path.isdir(l)]


def load_dsf(path):
    b = open(path, 'rb').read()
    if b[:2] == b'\x1f\x8b':
        b = gzip.decompress(b)
    return json.loads(b.decode('utf-8'))


def find_file(libs, rel):
    rel = urllib.parse.unquote(rel).lstrip('/')
    for l in libs:
        p = os.path.join(l, rel)
        if os.path.isfile(p):
            return p
    return None


URL_RE = re.compile(r'^(?P<scope>[^:]*):(?P<file>[^#]*)#(?P<id>[^?]+)\?(?P<prop>.+)$')


def parse_url(u):
    m = URL_RE.match(u)
    if not m:
        return None
    return m.group('scope'), urllib.parse.unquote(m.group('file')), urllib.parse.unquote(m.group('id')), m.group('prop')


def num(v):
    return ('%.6g' % float(v))


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0])
    ap.add_argument('morphs', help='<out>.morphs.txt written by daz_export.dsa (one corrective name per line)')
    ap.add_argument('out', help='correctives.txt to write')
    ap.add_argument('--lib', action='append', default=[], help='DAZ content library (default: DAZ Studio\'s from the registry)')
    a = ap.parse_args()
    libs = a.lib or libraries()
    if not libs:
        sys.exit('no DAZ content library found (pass --lib)')
    names = [l.strip() for l in open(a.morphs, encoding='utf-8-sig') if l.strip() and not l.startswith('#')]
    if not names:
        sys.exit('%s lists no correctives' % a.morphs)

    files = None                          # file name -> path for the listed correctives (found once by name)
    morphs = {}                           # key -> dict(default, min, max, clamped, shape, formulas=[(stage, tokens)])
    figure = None                         # (figure id, figure .dsf relative path)
    bones_used = set()
    todo = [('', n, None, True) for n in names]     # (scope, id, .dsf path relative to a library or None, is a blend shape)
    seen = set()
    missing = []

    def scope_of(sc):
        return '' if (not sc or sc.lower().startswith('genesis')) else sc

    while todo:
        scope, mid, rel, is_shape = todo.pop()
        if (scope, mid) in seen:
            continue
        seen.add((scope, mid))
        path = find_file(libs, rel) if rel else None
        if path is None and rel is None:
            if files is None:
                files = {}
                for l in libs:
                    for dp, _, fs in os.walk(os.path.join(l, 'data')):
                        for f in fs:
                            if f.lower().endswith('.dsf') and ('Morphs' in dp):
                                files.setdefault(f[:-4], []).append(os.path.join(dp, f))
        # a name can exist in several folders (a figure's shape packs ship their own adjustment morphs of the same name, without
        # drivers): the one whose modifier has formulas
        mod = None
        for cand in ([path] if path else (files or {}).get(mid, [])):
            d = load_dsf(cand)
            for m in d.get('modifier_library', []):
                if m.get('id') == mid or mod is None:
                    if mod is None or (m.get('formulas') and not mod.get('formulas')):
                        mod = m
            if mod is not None and mod.get('formulas'):
                break
        if mod is None:
            missing.append(mid)
            continue
        ch = mod.get('channel', {})
        key = (scope + ':' if scope else '') + mod.get('id', mid)
        ent = morphs.setdefault(key, dict(default=float(ch.get('value', 0.0)), min=float(ch.get('min', 0.0)), max=float(ch.get('max', 1.0)),
                                          clamped=1 if ch.get('clamped', True) else 0, shape=mid if is_shape else '-', formulas=[]))
        for fo in mod.get('formulas', []):
            out = parse_url(fo.get('output', ''))
            if out is None or out[3] != 'value':
                continue
            toks = []
            ok = True
            for op in fo.get('operations', []):
                o = op.get('op')
                if o == 'push' and 'url' in op:
                    u = parse_url(op['url'])
                    if u is None:
                        ok = False
                        break
                    sc, fl, uid, prop = u
                    if prop.startswith('rotation/'):
                        toks.append('r:%s:%s' % (uid, prop[-1]))
                        bones_used.add(uid)
                        if figure is None and fl.lower().endswith('.dsf'):
                            figure = (os.path.splitext(os.path.basename(fl))[0], fl)
                    elif prop == 'value':
                        ss = scope_of(sc)
                        toks.append('m:%s%s' % (ss + ':' if ss else '', uid))
                        todo.append((ss, uid, fl, False))
                    else:
                        ok = False
                        break
                elif o == 'push' and 'val' in op:
                    v = op['val']
                    if isinstance(v, list):
                        toks.append('k:%s,%s' % (num(v[0]), num(v[1])))
                    else:
                        toks.append('n:' + num(v))
                elif o in ('mult', 'add', 'sub', 'div'):
                    toks.append({'mult': '*', 'add': '+', 'sub': '-', 'div': '/'}[o])
                elif o in ('spline_tcb', 'spline_linear', 'spline_constant'):
                    toks.append({'spline_tcb': 'tcb', 'spline_linear': 'lin', 'spline_constant': 'const'}[o])
                else:
                    ok = False
                    break
            if ok and toks:
                ent['formulas'].append((fo.get('stage', 'sum'), toks))

    if figure is None:
        sys.exit('no corrective is driven by a bone rotation')
    fpath = find_file(libs, figure[1])
    if fpath is None:
        sys.exit('figure %s not found in %s' % (figure[1], '; '.join(libs)))
    fig = load_dsf(fpath)
    nodes = fig.get('node_library', [])

    def vec(n, k):
        v = {c['id']: float(c.get('value', 0.0)) for c in n.get(k, [])}
        return (v.get('x', 0.0), v.get('y', 0.0), v.get('z', 0.0))

    with open(a.out, 'w', encoding='utf-8', newline='\n') as f:
        f.write('# SfMorph correctives v1 (tools/sources/daz_correctives.py): DAZ joint-corrective drivers for the avatar mode\n')
        f.write('# from %s\n' % a.morphs)
        f.write('figure\t%s\t%s\n' % (figure[0] or '-', figure[1]))
        for n in nodes:
            if n.get('type', 'bone') != 'bone' and n.get('id') not in bones_used:
                continue
            par = (n.get('parent') or '#-').split('#')[-1]
            c = vec(n, 'center_point'); o = vec(n, 'orientation')
            # the rotation limits DAZ clamps each channel to (its formulas see the clamped angle)
            lim = []
            for ax in 'xyz':
                rc = next((c_ for c_ in n.get('rotation', []) if c_.get('id') == ax), {})
                lim += [num(rc.get('min', -1e4)), num(rc.get('max', 1e4))] if rc.get('clamped') else ['-10000', '10000']
            f.write('bone\t%s\t%s\t%s\t%s %s %s\t%s %s %s\t%s\n' % (n['id'], par, n.get('rotation_order', 'XYZ'), num(c[0]), num(c[1]), num(c[2]),
                                                                   num(o[0]), num(o[1]), num(o[2]), ' '.join(lim)))
        for key in sorted(morphs):
            m = morphs[key]
            f.write('morph\t%s\t%s\t%s\t%s\t%d\t%s\n' % (key, num(m['default']), num(m['min']), num(m['max']), m['clamped'], m['shape']))
            for stage, toks in m['formulas']:
                f.write('f\t%s\t%s\t%s\n' % (key, stage, ' '.join(toks)))
    shapes = sum(1 for m in morphs.values() if m['shape'] != '-')
    driven = sum(1 for m in morphs.values() if m['shape'] != '-' and m['formulas'])
    print('correctives: %d shapes (%d with drivers), %d helper morphs, %d driver bones, figure %s%s -> %s' % (
        shapes, driven, len(morphs) - shapes, len(bones_used), figure[1], ('; no .dsf for ' + ', '.join(sorted(set(missing)))) if missing else '', a.out))


if __name__ == '__main__':
    main()
