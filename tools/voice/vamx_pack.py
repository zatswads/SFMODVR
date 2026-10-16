import sys, os, re, shutil, wave, collections
# Usage: python -I vamx_pack.py <extracted vamX voice folder (WAV next to each mp3)> <moan categories folder or ''> <pack folder>
# Personal use only: never commit the audio.
# Builds an SF Body Morphs voice pack from an extracted vamX voice (WAV next to each mp3):
#   <dst>/<cat>/*.wav for the moan categories (from the survey's _by_category folder, short clips only)
#   <dst>/lines/<bank>[_<level>]/*.wav for spoken lines, classified by folder + filename transcript/tags.
src, bycat, dst = sys.argv[1], sys.argv[2], sys.argv[3]

def dur(p):
    try:
        w = wave.open(p); return w.getnframes() / float(w.getframerate())
    except Exception:
        return 99.0

OFFTOPIC = r'cloth|naked|nude|pose|toy|bullet|dildo|vibrat|69|lap ?dance|danc|couch|chair|\bvr\b|dark|bright|exercis|pushup|strip|game|button|menu|option|camera|light|cock|bj|blow ?job|suck|lick|eat|cum ?on|face|mouth|swallow|spank me|tie|rope|kinky ?slut|default|scenario|story|improv|\btop\b|take (this|it|my|that)? ?off|undress|i dont like you'
SAD = r'scared|sad|disgust|annoyed|bored|frustrat|angry|inpain|in ?pain|cry'

def words(n):
    # camel case and underscores -> lower words
    n = os.path.splitext(n)[0]
    n = re.sub(r'([a-z])([A-Z])', r'\1 \2', n)
    return re.sub(r'[_\-.]+', ' ', n).lower()

def level(w, cat):
    c = cat.lower()
    if 'higharousal' in w.replace(' ', '') or 'high arousal' in c or 'pre orgasm' in c or 'intense' in w: return 3
    if 'medium arousal' in c or re.search(r'med(ium)? ?arousal', w): return 2
    if 'low arousal' in c or re.search(r'(low|no) ?arousal', w): return 1
    if 'aroused' in w or 'pleasure' in w: return 2
    return 2

def classify(cat, fn):
    c = cat.lower(); w = words(fn)
    if c.startswith('emo/') and not c.startswith('emo/pain'): return None
    if c.startswith('game commands/'):
        g = c[len('game commands/'):]
        if g in ('ouch', 'spanking', 'pushed'): return 'ouch'
        if g in ('not yet', 'too close', 'i need space', 'disgusted', 'i dont like that', 'take a break', 'need soothing touch'): return 'guard'
        if re.search(OFFTOPIC, w): return None
        if 'breast' in g: return 'nipple'
        if 'pussy' in g and 'lick' not in g: return 'pussy'
        if g in ('make me cum', 'dont stop'): return 'request'
        if g in ('start sex',): return 'invite'
        if g in ('i like that', 'good', 'need sensual touch'): return 'public'
        return None
    if c.startswith('emo/pain'): return 'ouch'
    if re.search(OFFTOPIC, w): return None
    if re.search(r'\b(tits?|titties|tittis|breasts?|nipples?|boobs?)\b', w): return 'nipple'
    if re.search(r'\b(pussy|clit|fingers?|fingering)\b', w): return 'pussy'
    if re.search(r'\b(ass|anal|butt|asshole)\b', w): return 'anal'
    if c.startswith('seduction'): return 'invite'
    if c.startswith('pre orgasm'): return 'request'
    if c.startswith('post orgasm'): return 'afterglow'
    if 'arousal' in c and 'not thrusting' not in c: return 'public'
    return None

LEVELED = ('nipple', 'pussy', 'anal', 'public')
counts = collections.Counter()
lines_dir = os.path.join(dst, 'lines')
if os.path.isdir(lines_dir): shutil.rmtree(lines_dir)
for root, dirs, files in os.walk(src):
    rel = os.path.relpath(root, src).replace('\\', '/')
    parts = [p for p in rel.split('/') if p not in ('Phrase', 'Constant', 'Non-English', '.')]
    if '/Constant' in '/' + rel or 'Non-English' in rel: continue
    cat = '/'.join(parts)
    for f in files:
        if not f.lower().endswith('.wav'): continue
        b = classify(cat, f)
        if b is None: continue
        w = words(f)
        if b in LEVELED + ('invite', 'request', 'afterglow') and re.search(SAD, w): continue   # a pained or scared take is not a pleasure line
        p = os.path.join(root, f)
        d = dur(p)
        if d < 0.3 or d > (4.0 if b == 'ouch' else 7.0): continue
        folder = b + ('_%d' % level(w, cat) if b in LEVELED else '')
        od = os.path.join(lines_dir, folder)
        os.makedirs(od, exist_ok=True)
        shutil.copyfile(p, os.path.join(od, f))
        counts[folder] += 1

# moan categories from the survey (short clips only: vamX moan beds run for minutes)
CAP = {'breath_calm': 8, 'breath_heavy': 6, 'moan_low': 6, 'moan_mid': 6, 'moan_high': 6, 'climax': 14, 'exertion': 4}
for c, mx in CAP.items():
    sd = os.path.join(bycat, c)
    od = os.path.join(dst, c)
    if os.path.isdir(od): shutil.rmtree(od)
    if not os.path.isdir(sd): continue
    for f in os.listdir(sd):
        if not f.lower().endswith('.wav'): continue
        p = os.path.join(sd, f); d = dur(p)
        if d < 0.25 or d > mx: continue
        os.makedirs(od, exist_ok=True)
        shutil.copyfile(p, os.path.join(od, f))
        counts[c] += 1
for k in sorted(counts): print('%-14s %d' % (k, counts[k]))
