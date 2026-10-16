"""Her meshes by role, whatever variants the game has her in.

The game swaps her face, nipples, genitals, lashes, brows, hair and the pregnant bodies by customisation
(LadyCustomiseFunction: faceType[], nudeBody[pregnantNum], nipple / pussy types by SetActive), and SfMorph 0.11.1+ exports
every variant, worn or not (meshes.txt column 'active'). The pipeline fits the variant of each role she wears (else the
default one) and lets the other variants follow the fitted skin, so a character keeps working when the game swaps one; the
baked textures cover every face material.
"""
import re

PAT = {
    'upper': r'^reika_upperBody_\w+$',
    'lower': r'^reika_lowerBody_\w+$',
    'foot': r'^reika_foot$',
    'neck': r'^reika_neck$',
    'face': r'^reika_face_[A-Za-z0-9]+$',
    'nipple': r'^reika_nipple_type_\w+$',
    'nail': r'^reika_nail_\w+$',
    'lashes': r'^reika_eyelashes_\w+$',
    'brow': r'^reika_eyeBrow_\w+$',
    'tattoo': r'^lewdTattoos_\w+$',
    'pubic': r'^pubicHair_\w+$',
    'pussy': r'^pussy_type\w+$',
}
DEFAULT = {'upper': 'reika_upperBody_normal', 'lower': 'reika_lowerBody_normal', 'face': 'reika_face_a',
           'nipple': 'reika_nipple_type_1', 'nail': 'reika_nail_normal', 'pussy': 'pussy_typeC_normal'}
BODY_MAT = 'reika_body'
FOOT_MAT = 'reika_foot'


def base(name):
    """A mesh name without our clone suffixes."""
    return name.replace(' (SfSoft)', '').replace(' (SfMorph)', '')


def all_of(names, role):
    return sorted(n for n in names if re.match(PAT[role], base(n)))


def pick(names, role, active=()):
    """The variant of a role to fit: the one she wears, else the default, else the first; None when there is none."""
    c = all_of(names, role)
    act = [n for n in c if n in active]
    if act:
        return act[0]
    if DEFAULT.get(role) in c:
        return DEFAULT[role]
    return c[0] if c else None


def is_face_material(mat):
    return re.match(PAT['face'], mat) is not None


class Roles:
    """The primary mesh of each role and the variant lists, for a set of mesh names (and the ones she wears)."""

    def __init__(self, names, active=()):
        names = list(names)
        self.names = names
        self.upper = pick(names, 'upper', active)
        self.lower = pick(names, 'lower', active)
        self.foot = pick(names, 'foot', active)
        self.neck = pick(names, 'neck', active)
        self.face = pick(names, 'face', active)
        self.nipple = pick(names, 'nipple', active)
        self.faces = all_of(names, 'face')
        self.nipples = all_of(names, 'nipple')
        self.nails = all_of(names, 'nail')
        self.lashes = all_of(names, 'lashes')
        self.brows = all_of(names, 'brow')
        self.attach = self.nipples + self.nails + all_of(names, 'tattoo') + all_of(names, 'pubic')
        self.pussies = all_of(names, 'pussy')

    def fit(self):
        """sfpipe's FIT: her skin surface fitted onto the VaM body (mesh -> submesh materials)."""
        f = {}
        for n in (self.upper, self.lower, self.neck):
            if n:
                f[n] = {BODY_MAT}
        if self.foot:
            f[self.foot] = {BODY_MAT, FOOT_MAT}
        if self.face:
            f[self.face] = {self.face}
        return f

    def skin(self):
        """Her skin meshes of every variant (measuring, the slider fit)."""
        return [n for n in [self.upper, self.lower, self.foot, self.neck, self.face] if n]

    def body_sheet(self):
        """Meshes drawn with her body material (the body texture atlas): the bake target of 'reika_body'."""
        return [n for n in [self.upper, self.lower, self.foot, self.neck] if n] + self.nipples + self.nails + self.pussies
