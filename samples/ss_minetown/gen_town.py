#!/usr/bin/env python3
"""Generates ss_minetown.vmf: a TF2 mining town in a canyon. A main street
of storefronts opens into a plaza; the town's edges wander rather than run
straight, and each side gives a reason nobody walks out: a sheer, jagged
cliff behind the hotel and a steep hillside to the west, the canyon to the
east (barbed wire along its rim, a trestle bridge that ends in mid-air), a
ridge to the south (a rail spur running into a boarded-up adit, barricades
and wire) and the cliff face to the north, whose mine entrance leads down a
tunnel to a lit cavern and a shaft to the level below. Player clip over
the hills, ridge and cliff tops keeps blast jumpers in, and a fall into the
canyon is a death.

Coordinates: x east, y north, z up. Town ground at z=0, cavern floor at
z=-384. Player hull 48x48x82, step 18; doors are 128 wide, 160 tall.
"""
import math, sys

out_path = sys.argv[1] if len(sys.argv) > 1 else "ss_minetown.vmf"

ids = [0]
def nid():
    ids[0] += 1
    return ids[0]

solids = []
entities = []

def axes(n):
    ax, ay, az = abs(n[0]), abs(n[1]), abs(n[2])
    if az >= ax and az >= ay:
        return "[1 0 0 0] 0.25", "[0 -1 0 0] 0.25"
    if ax >= ay:
        return "[0 1 0 0] 0.25", "[0 0 -1 0] 0.25"
    return "[1 0 0 0] 0.25", "[0 0 -1 0] 0.25"

def sub(a, b): return (a[0]-b[0], a[1]-b[1], a[2]-b[2])
def cross(a, b): return (a[1]*b[2]-a[2]*b[1], a[2]*b[0]-a[0]*b[2], a[0]*b[1]-a[1]*b[0])
def dot(a, b): return a[0]*b[0]+a[1]*b[1]+a[2]*b[2]

def convex(faces, centroid, mats, scale=0.25):
    """faces: list of 3-point tuples on each plane; orientation fixed so the
    plane's normal points away from the centroid (Hammer: points clockwise
    seen from outside, normal = (p2-p0) x (p1-p0))."""
    sides = []
    for i, (p0, p1, p2) in enumerate(faces):
        n = cross(sub(p2, p0), sub(p1, p0))
        if dot(n, sub(p0, centroid)) < 0:
            p1, p2 = p2, p1
            n = cross(sub(p2, p0), sub(p1, p0))
        u, v = axes(n)
        if scale != 0.25:
            u = u.replace("0.25", str(scale)); v = v.replace("0.25", str(scale))
        mat = mats[i] if isinstance(mats, list) else mats
        f = lambda p: f"({p[0]:g} {p[1]:g} {p[2]:g})"
        sides.append(f'\t\tside\n\t\t{{\n\t\t\t"id" "{nid()}"\n\t\t\t"plane" "{f(p0)} {f(p1)} {f(p2)}"\n'
                     f'\t\t\t"material" "{mat.upper()}"\n\t\t\t"uaxis" "{u}"\n\t\t\t"vaxis" "{v}"\n'
                     f'\t\t\t"rotation" "0"\n\t\t\t"lightmapscale" "16"\n\t\t\t"smoothing_groups" "0"\n\t\t}}\n')
    return f'\tsolid\n\t{{\n\t\t"id" "{nid()}"\n' + "".join(sides) + "\t}\n"

def box(x1, y1, z1, x2, y2, z2, mat, top=None, bottom=None, scale=0.25):
    x1, x2 = min(x1, x2), max(x1, x2); y1, y2 = min(y1, y2), max(y1, y2); z1, z2 = min(z1, z2), max(z1, z2)
    if x2 - x1 < 1 or y2 - y1 < 1 or z2 - z1 < 1:
        return
    c = ((x1+x2)/2, (y1+y2)/2, (z1+z2)/2)
    faces = [((x1,y1,z2),(x2,y1,z2),(x2,y2,z2)), ((x1,y1,z1),(x2,y1,z1),(x2,y2,z1)),
             ((x1,y1,z1),(x1,y2,z1),(x1,y2,z2)), ((x2,y1,z1),(x2,y2,z1),(x2,y2,z2)),
             ((x1,y1,z1),(x2,y1,z1),(x2,y1,z2)), ((x1,y2,z1),(x2,y2,z1),(x2,y2,z2))]
    mats = [top or mat, bottom or mat, mat, mat, mat, mat]
    solids.append(convex(faces, c, mats, scale))

def ramp_y(x1, x2, y1, y2, zbot, ztop1, ztop2, mat, top=None):
    """A slab whose top runs from ztop1 at y1 to ztop2 at y2, bottom flat at zbot."""
    c = ((x1+x2)/2, (y1+y2)/2, (zbot + (ztop1+ztop2)/2)/2)
    faces = [((x1,y1,ztop1),(x2,y1,ztop1),(x2,y2,ztop2)),  # sloped top
             ((x1,y1,zbot),(x2,y1,zbot),(x2,y2,zbot)),
             ((x1,y1,zbot),(x1,y2,zbot),(x1,y2,ztop2)),
             ((x2,y1,zbot),(x2,y2,zbot),(x2,y2,ztop2)),
             ((x1,y1,zbot),(x2,y1,zbot),(x2,y1,ztop1)),
             ((x1,y2,zbot),(x2,y2,zbot),(x2,y2,ztop2))]
    solids.append(convex(faces, c, [top or mat] + [mat]*5))

def slab_y(x1, x2, y1, y2, zb1, zb2, thick, mat):
    """A sloped ceiling slab: underside from zb1 at y1 to zb2 at y2, thickness up."""
    c = ((x1+x2)/2, (y1+y2)/2, (zb1+zb2)/2 + thick/2)
    faces = [((x1,y1,zb1+thick),(x2,y1,zb1+thick),(x2,y2,zb2+thick)),
             ((x1,y1,zb1),(x2,y1,zb1),(x2,y2,zb2)),
             ((x1,y1,zb1),(x1,y2,zb2),(x1,y2,zb2+thick)),
             ((x2,y1,zb1),(x2,y2,zb2),(x2,y2,zb2+thick)),
             ((x1,y1,zb1),(x2,y1,zb1),(x2,y1,zb1+thick)),
             ((x1,y2,zb2),(x2,y2,zb2),(x2,y2,zb2+thick))]
    solids.append(convex(faces, c, mat))

def gable(x1, x2, y1, y2, zbase, rise, mat, ridge_along_x):
    """A pitched roof prism over a footprint."""
    if ridge_along_x:
        ym = (y1+y2)/2
        c = ((x1+x2)/2, ym, zbase + rise/3)
        faces = [((x1,y1,zbase),(x2,y1,zbase),(x2,ym,zbase+rise)),
                 ((x1,y2,zbase),(x2,y2,zbase),(x2,ym,zbase+rise)),
                 ((x1,y1,zbase),(x2,y1,zbase),(x2,y2,zbase)),
                 ((x1,y1,zbase),(x1,y2,zbase),(x1,ym,zbase+rise)),
                 ((x2,y1,zbase),(x2,y2,zbase),(x2,ym,zbase+rise))]
    else:
        xm = (x1+x2)/2
        c = (xm, (y1+y2)/2, zbase + rise/3)
        faces = [((x1,y1,zbase),(x1,y2,zbase),(xm,y2,zbase+rise)),
                 ((x2,y1,zbase),(x2,y2,zbase),(xm,y2,zbase+rise)),
                 ((x1,y1,zbase),(x2,y1,zbase),(x2,y2,zbase)),
                 ((x1,y1,zbase),(x2,y1,zbase),(xm,y1,zbase+rise)),
                 ((x1,y2,zbase),(x2,y2,zbase),(xm,y2,zbase+rise))]
    solids.append(convex(faces, c, mat))

def disp_chunk(n, heights, alphas=None):
    """A dispinfo chunk for a top face starting at its low-x low-y corner:
    rows run along +y, columns along +x, distances along +z."""
    def kv(name, vals):
        return f'\t\t\t\t\t"{name}" "{vals}"\n'
    normals = ''.join(kv(f"row{r}", " ".join(["0 0 1"] * n)) for r in range(n))
    dist = ''.join(kv(f"row{r}", " ".join(f"{heights[r][c]:.2f}" for c in range(n))) for r in range(n))
    zeros = ''.join(kv(f"row{r}", " ".join(["0 0 0"] * n)) for r in range(n))
    alph = ''.join(kv(f"row{r}", " ".join(f"{(alphas[r][c] if alphas else 0):.0f}" for c in range(n))) for r in range(n))
    tags = ''.join(kv(f"row{r}", " ".join(["9"] * (2 * (n - 1)))) for r in range(n - 1))
    return ('\t\t\tdispinfo\n\t\t\t{\n\t\t\t\t"power" "3"\n\t\t\t\t"startposition" "[%s]"\n\t\t\t\t"flags" "0"\n'
            '\t\t\t\t"elevation" "0"\n\t\t\t\t"subdiv" "0"\n'
            f'\t\t\t\tnormals\n\t\t\t\t{{\n{normals}\t\t\t\t}}\n\t\t\t\tdistances\n\t\t\t\t{{\n{dist}\t\t\t\t}}\n'
            f'\t\t\t\toffsets\n\t\t\t\t{{\n{zeros}\t\t\t\t}}\n\t\t\t\toffset_normals\n\t\t\t\t{{\n{normals}\t\t\t\t}}\n'
            f'\t\t\t\talphas\n\t\t\t\t{{\n{alph}\t\t\t\t}}\n\t\t\t\ttriangle_tags\n\t\t\t\t{{\n{tags}\t\t\t\t}}\n'
            '\t\t\t\tallowed_verts\n\t\t\t\t{\n\t\t\t\t\t"10" "-1 -1 -1 -1 -1 -1 -1 -1 -1 -1"\n\t\t\t\t}\n\t\t\t}\n')

def terrain(x1, y1, x2, y2, height, mat, side="tools/toolsnodraw"):
    """A 64-unit-thick slab under z = 0 whose top is a power-3 displacement
    following height(x, y); every other side nodraw."""
    n = 9
    hs = [[height(x1 + (x2 - x1) * c / (n - 1), y1 + (y2 - y1) * r / (n - 1)) for c in range(n)] for r in range(n)]
    # Displacements neither seal the map nor fill space: a solid filler up to
    # just below the surface's lowest point keeps the space under the ground
    # out of the world, as it would be in Hammer.
    # A flat tile gets one filler; a hilly one a 4 x 4 grid of them, each
    # topped just under the lowest of its own patch of surface.
    lowest = min(min(r) for r in hs)
    highest = max(max(r) for r in hs)
    parts = 1 if highest - lowest < 32 else 4
    step = (n - 1) // parts
    for pr in range(parts):
        for pc in range(parts):
            patch = [hs[r][c] for r in range(pr * step, pr * step + step + 1) for c in range(pc * step, pc * step + step + 1)]
            top = min(patch) - 16
            if top > -728:
                fx1 = x1 + (x2 - x1) * pc / parts
                fy1 = y1 + (y2 - y1) * pr / parts
                box(fx1, fy1, -744, fx1 + (x2 - x1) / parts, fy1 + (y2 - y1) / parts, top, "tools/toolsnodraw")
    box(x1, y1, -64, x2, y2, 0, side, top=mat)
    sol = solids.pop()
    marker = '\t\t\t"smoothing_groups" "0"\n'
    first = sol.index(marker) + len(marker)
    chunk = disp_chunk(n, hs) % f"{x1:g} {y1:g} 0"
    solids.append(sol[:first] + chunk + sol[first:])
    return max(max(r) for r in hs)

def wedge(points_bottom, points_top, mat):
    """A convex prism from a bottom and a top quad (each four points in the
    same order), for slanted rock faces."""
    pts = points_bottom + points_top
    c = tuple(sum(p[i] for p in pts) / len(pts) for i in range(3))
    b, t = points_bottom, points_top
    faces = [(b[0], b[1], b[2]), (t[0], t[1], t[2])]
    for i in range(4):
        j = (i + 1) % 4
        faces.append((b[i], b[j], t[j]))
    solids.append(convex(faces, c, mat))

def brush_entity(classname, lo, hi, mat, **kv):
    """An entity with one box brush (a trigger or clip volume)."""
    box(lo[0], lo[1], lo[2], hi[0], hi[1], hi[2], mat)
    sol = solids.pop()
    entities.append({"classname": classname, **{k: str(v) for k, v in kv.items()}, "__solid": sol})

def ent(classname, origin=None, **kv):
    e = {"classname": classname}
    if origin is not None:
        e["origin"] = f"{origin[0]:g} {origin[1]:g} {origin[2]:g}"
    e.update({k: str(v) for k, v in kv.items()})
    entities.append(e)

def prop(model, origin, yaw=0, cls="prop_static", pitch=0, **kv):
    ent(cls, origin, model=f"models/{model}", angles=f"{pitch:g} {yaw:g} 0", solid=6, skin=kv.pop("skin", 0), **kv)

def light(origin, color="255 214 170", brightness=180, **kv):
    ent("light", origin, _light=f"{color} {brightness}", _quadratic_attn=1, **kv)

def wall_with_openings(axis, fixed, t, a1, a2, z1, z2, openings, mat, inner=None):
    """A wall along `axis` ('x' or 'y') between a1..a2, thickness t from fixed,
    with rectangular openings [(a_lo, a_hi, z_lo, z_hi)]. Openings may share a
    span along the wall (a door with a window above it, two rows of windows):
    the wall is cut into vertical strips at every opening edge, and each strip
    is filled between the openings that cross it."""
    def piece(b1, b2, zl, zh):
        if b2 - b1 < 1 or zh - zl < 1:
            return
        if axis == 'x':
            box(b1, fixed, zl, b2, fixed + t, zh, mat)
        else:
            box(fixed, b1, zl, fixed + t, b2, zh, mat)
    edges = sorted({a1, a2, *[o[0] for o in openings], *[o[1] for o in openings]})
    edges = [e for e in edges if a1 <= e <= a2]
    for b1, b2 in zip(edges, edges[1:]):
        holes = sorted((zl, zh) for (lo, hi, zl, zh) in openings if lo <= b1 and hi >= b2)
        z = z1
        for (zl, zh) in holes:
            piece(b1, b2, z, zl)
            z = max(z, zh)
        piece(b1, b2, z, z2)

# ---------------------------------------------------------------- world shell
SKY = "tools/toolsskybox"
X0, X1, Y0, Y1, ZB, ZT = -2304, 2304, -2816, 4096, -1280, 1280
T = 16
box(X0-T, Y0-T, ZT, X1+T, Y1+T, ZT+T, SKY)
box(X0-T, Y0-T, ZB-T, X1+T, Y1+T, ZB, "tools/toolsnodraw")
box(X0-T, Y0-T, ZB, X0, Y1+T, ZT, SKY)
box(X1, Y0-T, ZB, X1+T, Y1+T, ZT, SKY)
box(X0, Y0-T, ZB, X1, Y0, ZT, SKY)
box(X0, Y1, ZB, X1, Y1+T, ZT, SKY)

DIRT = "nature/dirtground003"
ROCK = "nature/rockwall008"
ROCKTOP = "nature/rockground004"
WOODFLOOR = "wood/wood_floor002"
BEAM = "wood/wood_beam02"
ROOF = "cp_manor/wood_shingles01"
WALLS = ["wood/wood_wall004", "wood/wood_wall006", "wood/wood_wall007", "wood/wood_wall009", "wood/wood_wall005a", "wood/wood_wall008"]

TX0, TX1, TY0, TY1 = -1536, 1536, -2304, 1536

def curve(points, t):
    """A smooth line through control points [(t, value)], flat beyond the
    ends: cosine easing between neighbours, so the edge bends rather than
    kinks."""
    if t <= points[0][0]:
        return points[0][1]
    for (t0, v0), (t1, v1) in zip(points, points[1:]):
        if t <= t1:
            f = (1 - math.cos(math.pi * (t - t0) / (t1 - t0))) / 2
            return v0 + (v1 - v0) * f
    return points[-1][1]

def slope_of(fn, t, h=8.0):
    return (fn(t + h) - fn(t - h)) / (2 * h)

# The town's boundary is three wandering lines rather than a rectangle. The
# west line bulges back behind the hotel and becomes a sheer cliff from the
# saloon to the hardware store; the east line follows the canyon rim and
# swings out round the assay office; the south line dips round the spawn
# depot and climbs toward both corners, where the ridge wraps in.
WEST_PTS = [(-2816, -1010), (-2300, -1060), (-1800, -1115), (-1350, -1090), (-1000, -1125),
            (-720, -1440), (-300, -1500), (120, -1470), (380, -1250), (760, -1190), (1050, -1330), (1536, -1440)]
EAST_PTS = [(-2816, 1080), (-2300, 1120), (-1750, 1060), (-1250, 1100), (-820, 1270), (-300, 1370),
            (120, 1340), (450, 1215), (820, 1260), (1100, 1440), (1300, 1536)]
SOUTH_PTS = [(-2304, -2120), (-1500, -2160), (-900, -2290), (-450, -2335), (450, -2335), (900, -2280), (1500, -2150), (2304, -2100)]
CLIFF_Y0, CLIFF_Y1 = -980, 640     # the stretch of the west edge that is a sheer cliff

def west_edge(y): return curve(WEST_PTS, y)
def east_edge(y): return curve(EAST_PTS, y)
def south_edge(x): return curve(SOUTH_PTS, x)

def rise(d, cap, grade):
    """A hillside rising `grade` units per unit out from its foot, eased in
    over the first 48 units so the foot is a curve rather than a crease, and
    levelling off at `cap`."""
    if d <= 0:
        return 0.0
    ease = 48.0
    h = grade * d * d / (2 * ease) if d < ease else grade * (ease / 2 + d - ease)
    return min(cap, h)

def ground(x, y):
    """The canyon floor's height: flat through the town and rising or
    falling outside it. The three sides are added rather than chosen, so
    where two meet (the ridge running into the west hill, or into the
    canyon) the surfaces join without a seam or a V-shaped trough."""
    rough = 28 * math.sin(x / 170.0) * math.cos(y / 210.0) + 14 * math.sin((x + 2 * y) / 95.0)
    dw, de, ds = west_edge(y) - x, x - east_edge(y), south_edge(x) - y
    h = rise(dw, 560, 1.9) + rise(ds, 520, 1.8) - rise(de, 680, 3.0)
    h = min(h, 640.0)
    # The canyon's far wall, which the clip keeps anyone from reaching.
    h += rise(x - 1960, 1100, 2.6)
    out = max(dw, de, ds)
    if out > 0:
        return h + rough * min(1.0, out / 120.0)
    return rough * 0.25 * max(0.0, 1 - (-out) / 160.0)

for gx in range(X0, X1, 512):
    for gy in range(Y0, TY1, 512):
        x2, y2 = gx + 512, min(gy + 512, TY1)
        corners = [ground(gx, gy), ground(x2, gy), ground(gx, y2), ground(x2, y2), ground(gx + 256, gy + 256)]
        steep = max(corners) - min(corners) > 120 or max(corners) > 150 or min(corners) < -150
        terrain(gx, gy, x2, y2, ground, ROCK if steep else DIRT)
# A sealed floor under the terrain (displacements do not seal the map) and
# a worn strip down the street.
box(X0, Y0, -760, X1, TY1, -744, "tools/toolsnodraw")
box(-256, -2300, 0, 256, TY1 - 512, 1, "nature/dirtground006")

# The west cliff: slabs of rock standing on the edge line, each leaning
# back by its own amount and topped at its own height, so the face is
# jagged and stepped. They are tallest mid-run and lower toward the ends,
# where the hillside takes over.
for y0 in range(CLIFF_Y0, CLIFF_Y1, 128):
    y1 = y0 + 128
    xe0, xe1 = west_edge(y0), west_edge(y1)
    taper = min(1.0, (y0 - CLIFF_Y0 + 128) / 384.0, (CLIFF_Y1 - y0) / 384.0)
    top = 220 + taper * (260 + 60 * math.sin(y0 * 0.023))
    lean = 40 + 36 * (1 + math.sin(y0 * 0.017 + 1.3))
    back = min(xe0, xe1) - 420
    wedge([(xe0, y0, -32), (xe1, y1, -32), (back, y1, -32), (back, y0, -32)],
          [(xe0 - lean, y0, top), (xe1 - lean, y1, top), (back, y1, top), (back, y0, top)], ROCK)

# Player clip over the hills, the ridge, the cliff and the canyon's far
# wall, as TF2 maps do, so blast jumpers cannot climb out of the town. These
# are world brushes: player clip in the world blocks players only.
CLIP = "tools/toolsplayerclip"
for y in range(Y0, TY1, 128):
    edge = max(west_edge(y), west_edge(y + 128))
    cliff = CLIFF_Y0 <= y < CLIFF_Y1
    box(X0, y, 360 if cliff else 380, edge - (0 if cliff else 200), y + 128, ZT - 16, CLIP)
for x in range(-2304, 2304, 128):
    edge = max(south_edge(x), south_edge(x + 128))
    box(x, Y0, 380, x + 128, edge - 200, ZT - 16, CLIP)
box(2000, Y0, -320, X1, TY1, ZT - 16, CLIP)
# Over the north cliff's top, so nobody stands on the plateau.
box(X0, TY1 - 32, 560, X1, Y1, ZT - 16, CLIP)
# A fall into the canyon is a death: kill volumes along its floor, one per
# strip so each follows the rim.
for y in range(Y0, TY1, 256):
    rim = min(east_edge(y), east_edge(y + 256))
    brush_entity("trigger_hurt", (rim + 190, y, -760), (2000, y + 256, -520), "tools/toolstrigger", damage=1000, damagetype=32, spawnflags=1)

# ---------------------------------------------------------------- north cliff, tunnel and cavern
CY0, CY1 = TY1, TY1 + 96          # cliff face slab
MX = 160                           # tunnel half width (interior x -160..160)
MZ = 224                           # tunnel interior height
cliff_top = 768
TZ = 96                            # mine terrace height
TER0 = TY1 - 512                   # terrace south edge
# Cliff face with the mine mouth cut out at terrace level.
wall_with_openings('x', CY0, CY1 - CY0, X0, X1, -760, cliff_top, [(-MX, MX, TZ, TZ + MZ)], ROCK)
# The terrace: a raised shelf across the town's north end, reached by stairs
# on both sides of a central rail ramp.
box(TX0, TER0, -64, TX1, TY1, TZ, "wood/wood_wall004", top=DIRT)
for k in range(6):
    yk = TER0 - (6 - k) * 32
    box(-640, yk, 0, -160, TER0, (k + 1) * 16, "wood/wood_floor003")
    box(160, yk, 0, 640, TER0, (k + 1) * 16, "wood/wood_floor003")
ramp_y(-160, 160, TER0 - 576, TER0, -16, 0, TZ, "wood/wood_wall004", top=DIRT)
for x in (-176, 160):
    ramp_y(x, x + 16, TER0 - 576, TER0, -16, 16, TZ + 16, BEAM)
# Jagged rock faces leaning on the cliff, either side of the mine terrace.
for i, xs in enumerate(range(TX0, TX1, 192)):
    if -560 < xs + 96 < 560:
        continue
    d1 = 80 + 70 * (math.sin(xs * 0.013) + 1)
    d2 = 80 + 70 * (math.sin((xs + 192) * 0.013) + 1)
    top = cliff_top - 40 - 60 * (1 + math.sin(xs * 0.021))
    wedge([(xs, CY0 - d1, TZ), (xs + 192, CY0 - d2, TZ), (xs + 192, CY0, TZ), (xs, CY0, TZ)],
          [(xs, CY0 - 24, top), (xs + 192, CY0 - 24, top + 30), (xs + 192, CY0, top + 30), (xs, CY0, top)], ROCK)
# Plateau above everything north of the cliff.
box(X0, CY1, cliff_top - 32, X1, Y1, cliff_top, ROCK, top=ROCKTOP)

# Tunnel: level entry 256 long, then a ramp down to the cavern.
TL0, TL1 = CY1, CY1 + 256          # level section
RY0, RY1 = TL1, TL1 + 768          # ramp section
CAVZ = -384
box(-MX, TL0, -64, MX, TL1, TZ, DIRT)                               # floor
box(-MX - 32, TL0, -64, -MX, TL1, TZ + MZ, ROCK); box(MX, TL0, -64, MX + 32, TL1, TZ + MZ, ROCK)
box(-MX - 32, TL0, TZ + MZ, MX + 32, TL1, TZ + MZ + 32, ROCK)
ramp_y(-MX, MX, RY0, RY1, CAVZ - 64, TZ, CAVZ, ROCK, top=DIRT)
box(-MX - 32, RY0, CAVZ - 64, -MX, RY1, TZ + MZ, ROCK); box(MX, RY0, CAVZ - 64, MX + 32, RY1, TZ + MZ, ROCK)
slab_y(-MX - 32, MX + 32, RY0, RY1, TZ + MZ, CAVZ + MZ, 32, ROCK)

# Cavern: x -896..896, y RY1..RY1+1024, floor z=-384, ceiling z=-64.
CX0, CX1, CY2, CY3, CTOP = -896, 896, RY1, RY1 + 1024, -96
GY0, GY1, GX1 = CY2 + 256, CY2 + 640, CX1 + 448           # east side gallery
box(CX0 - 32, CY2, CAVZ - 64, CX0, CY3, CTOP, ROCK)
wall_with_openings('y', CX1, 32, CY2, CY3, CAVZ - 64, CTOP, [(GY0, GY1, CAVZ, CAVZ + 192)], ROCK)
box(CX1 + 32, GY0, CAVZ - 64, GX1, GY1, CAVZ, DIRT)
box(CX1 + 32, GY0, CAVZ + 192, GX1, GY1, CAVZ + 224, ROCK)
box(GX1, GY0 - 32, CAVZ - 64, GX1 + 32, GY1 + 32, CAVZ + 224, ROCK)
box(CX1 + 32, GY0 - 32, CAVZ - 64, GX1, GY0, CAVZ + 224, ROCK)
box(CX1 + 32, GY1, CAVZ - 64, GX1, GY1 + 32, CAVZ + 224, ROCK)
for y in range(CY2 + 128, CY3 - 64, 256):
    box(CX0, y, CAVZ, CX0 + 16, y + 16, CTOP, BEAM)
    box(CX1 - 16, y, CAVZ, CX1, y + 16, CTOP, BEAM)
    box(CX0, y, CTOP - 20, CX1, y + 16, CTOP, BEAM)
for y in (GY0 + 96, GY1 - 112):
    box(CX1 + 32, y, CAVZ + 176, GX1, y + 16, CAVZ + 192, BEAM)
box(CX0 - 32, CY3, CAVZ - 64, CX1 + 32, CY3 + 32, CTOP, ROCK)
wall_with_openings('x', CY2 - 32, 32, CX0 - 32, CX1 + 32, CAVZ - 64, CTOP, [(-MX, MX, CAVZ, CAVZ + MZ)], ROCK)
box(CX0 - 32, CY2 - 32, CTOP, CX1 + 32, CY3 + 32, CTOP + 32, ROCK)
# The shaft to the next level: a 256x256 pit at the cavern's north end, 640 deep,
# ringed by a timber collar.
SX0, SX1, SY0, SY1 = -128, 128, CY3 - 448, CY3 - 192
# The cavern floor, built around the pit.
for (x1, y1, x2, y2) in [(CX0, CY2, CX1, SY0), (CX0, SY1, CX1, CY3), (CX0, SY0, SX0, SY1), (SX1, SY0, CX1, SY1)]:
    box(x1, y1, CAVZ - 64, x2, y2, CAVZ, DIRT)
PITZ = CAVZ - 640
box(SX0 - 32, SY0 - 32, PITZ - 32, SX1 + 32, SY1 + 32, PITZ, ROCK)
for (x1, y1, x2, y2) in [(SX0 - 32, SY0 - 32, SX1 + 32, SY0), (SX0 - 32, SY1, SX1 + 32, SY1 + 32), (SX0 - 32, SY0, SX0, SY1), (SX1, SY0, SX1 + 32, SY1)]:
    box(x1, y1, PITZ, x2, y2, CAVZ - 64, ROCK)
# Timber collar around the pit mouth.
for (x1, y1, x2, y2) in [(SX0 - 16, SY0 - 16, SX1 + 16, SY0), (SX0 - 16, SY1, SX1 + 16, SY1 + 16), (SX0 - 16, SY0, SX0, SY1), (SX1, SY0, SX1 + 16, SY1)]:
    box(x1, y1, CAVZ, x2, y2, CAVZ + 24, BEAM)
# Headframe posts and beam over the shaft.
for (x, y) in [(SX0 - 16, SY0 - 16), (SX1, SY0 - 16), (SX0 - 16, SY1), (SX1, SY1)]:
    box(x, y, CAVZ, x + 16, y + 16, CAVZ + 288, BEAM)
box(SX0 - 16, SY0 - 16, CAVZ + 288, SX1 + 16, SY0, CAVZ + 304, BEAM)
box(SX0 - 16, SY1, CAVZ + 288, SX1 + 16, SY1 + 16, CAVZ + 304, BEAM)
box(-8, SY0 - 16, CAVZ + 288, 8, SY1 + 16, CAVZ + 304, BEAM)
# Nobody climbs out of the shaft: a fall to the next level is a respawn.
brush_entity("trigger_hurt", (SX0, SY0, PITZ), (SX1, SY1, PITZ + 96), "tools/toolstrigger", damage=1000, damagetype=32, spawnflags=1)
# Sign at the shaft.
box(-64, SY0 - 40, CAVZ + 180, 64, SY0 - 36, CAVZ + 244, "signs/lowerlevel_red")
box(-24, SY0 - 22, CAVZ + 28, 24, SY0 - 18, CAVZ + 76, "signs/arrow_dn_red")

# ---------------------------------------------------------------- storefronts
def store(x1, x2, y1, y2, street_side, height, wall_mat, sign_mat=None, name="", interior=None):
    """A western storefront: four walls, a door and windows on the street
    side, a false front rising above a pitched roof, a porch and awning."""
    t = 16
    door_w, door_h = 128, 160
    yc = (y1 + y2) / 2
    # Floor.
    box(x1, y1, 0, x2, y2, 8, WOODFLOOR)
    front_x = x2 - t if street_side == "east" else x1
    back_x = x1 if street_side == "east" else x2 - t
    openings = [(yc - door_w / 2, yc + door_w / 2, 8, door_h),
                (y1 + 48, y1 + 48 + 96, 72, 152), (y2 - 48 - 96, y2 - 48, 72, 152)]
    if height > 320:
        openings = [(yc - door_w / 2, yc + door_w / 2, 8, door_h),
                    (y1 + 48, y1 + 48 + 96, 72, 152), (y2 - 48 - 96, y2 - 48, 72, 152),
                    (y1 + 48, y1 + 48 + 96, 232, 312), (yc - 48, yc + 48, 232, 312), (y2 - 48 - 96, y2 - 48, 232, 312)]
    wall_with_openings('y', front_x, t, y1, y2, 0, height + 96, openings, wall_mat)
    wall_with_openings('y', back_x, t, y1, y2, 0, height, [(yc - 32, yc + 32, 72, 136)], wall_mat)
    wall_with_openings('x', y1, t, x1 + t, x2 - t, 0, height, [((x1 + x2) / 2 - 40, (x1 + x2) / 2 + 40, 72, 136)], wall_mat)
    wall_with_openings('x', y2 - t, t, x1 + t, x2 - t, 0, height, [], wall_mat)
    # Ceiling and pitched roof (ridge running along the street).
    box(x1, y1, height, x2, y2, height + 8, WOODFLOOR)
    gable(x1 - 16, x2 + 16, y1 - 16, y2 + 16, height + 8, 96, ROOF, ridge_along_x=False)
    # Porch and awning on the street side.
    px1, px2 = (x2, x2 + 128) if street_side == "east" else (x1 - 128, x1)
    box(px1, y1, 0, px2, y2, 12, WOODFLOOR)
    box(px1, y1, 168, px2, y2, 176, ROOF if height <= 320 else WOODFLOOR)
    if height > 320:
        # A balcony on the porch roof, with a rail.
        rx = px2 - 8 if street_side == "east" else px1
        box(rx, y1, 176, rx + 8, y2, 212, BEAM)
    for y in (y1 + 8, yc - 8, y2 - 24):
        xpost = px2 - 16 if street_side == "east" else px1
        box(xpost, y, 12, xpost + 16, y + 16, 168, BEAM)
    # Sign board on the false front.
    if sign_mat:
        sx = x2 + 2 if street_side == "east" else x1 - 6
        box(sx, yc - 96, height + 8, sx + 4, yc + 96, height + 88, sign_mat)
    # Interior light and lamp.
    light(((x1 + x2) / 2, yc, height - 40), brightness=160)
    prop("props_2fort/lantern001.mdl", ((x1 + x2) / 2, yc, height - 24), cls="prop_dynamic")
    # Porch lantern.
    light(((px1 + px2) / 2, yc, 150), color="255 200 140", brightness=60)
    if interior:
        interior(x1 + t, x2 - t, y1 + t, y2 - t)

def general_store(x1, x2, y1, y2):
    for i, y in enumerate(range(int(y1) + 32, int(y2) - 64, 72)):
        prop("props_farm/shelf_props01.mdl", (x1 + 24, y + 24, 8), yaw=0)
    box((x1 + x2) / 2 - 24, y1 + 96, 8, (x1 + x2) / 2 + 24, y2 - 96, 48, "wood/wood_floor003")
    prop("props_farm/wooden_barrel.mdl", (x2 - 48, y1 + 48, 8))
    prop("props_2fort/miningcrate001.mdl", (x2 - 56, y2 - 64, 8), yaw=20)
    prop("props_farm/tools_rack.mdl", (x2 - 20, (y1 + y2) / 2, 8), yaw=180)

def saloon(x1, x2, y1, y2):
    box(x1 + 24, y1 + 48, 8, x1 + 72, y2 - 48, 52, "wood/wood_floor003")  # bar counter
    for i in range(3):
        prop("props_trainyard/beer_keg001.mdl", (x1 + 40, y1 + 80 + i * 96, 52))
    for (dx, dy) in [(0.6, 0.3), (0.6, 0.7)]:
        x, y = x1 + (x2 - x1) * dx, y1 + (y2 - y1) * dy
        box(x - 32, y - 32, 32, x + 32, y + 32, 40, "wood/wood_floor003")
        box(x - 4, y - 4, 8, x + 4, y + 4, 32, BEAM)

def assay(x1, x2, y1, y2):
    prop("props_2fort/miningcrate001.mdl", (x1 + 64, y1 + 64, 8))
    prop("props_2fort/miningcrate002.mdl", (x1 + 64, y2 - 64, 8), yaw=90)
    prop("props_mining/ingot001.mdl", ((x1 + x2) / 2, (y1 + y2) / 2, 8))
    prop("props_2fort/pick001.mdl", (x2 - 40, (y1 + y2) / 2, 8), yaw=30)

def hardware(x1, x2, y1, y2):
    prop("props_farm/tools_rack.mdl", (x1 + 20, (y1 + y2) / 2, 8))
    prop("props_farm/spool_rope.mdl", (x2 - 64, y1 + 64, 8))
    prop("props_farm/spool_wire.mdl", (x2 - 64, y2 - 64, 8))
    prop("props_2fort/oildrum.mdl", (x2 - 140, (y1 + y2) / 2, 8))

# West side (fronts face east, toward the street). The street opens into a
# plaza between y -640 and 256, framed by the hotel and the bank.
stores_w = [(-960, -400, -1792, -1312, WALLS[0], "signs/sign_post", general_store, 256),
            (-960, -400, -1184, -768, WALLS[1], "signs/sign_bar", saloon, 288),
            (-1344, -704, -576, 128, WALLS[3], "signs/billboard001", saloon, 384),
            (-1056, -496, 352, 736, WALLS[2], None, hardware, 256)]
for (x1, x2, y1, y2, wm, sm, inter, h) in stores_w:
    store(x1, x2, y1, y2, "east", h, wm, sm, interior=inter)
stores_e = [(400, 960, -1792, -1312, WALLS[4], "signs/blufreight", hardware, 288),
            (400, 960, -1184, -768, WALLS[5], None, general_store, 256),
            (704, 1216, -512, 64, "concrete/wall011", "signs/sign_barracks", assay, 320),
            (528, 1088, 416, 800, WALLS[0], "signs/red_barn", assay, 288)]
for (x1, x2, y1, y2, wm, sm, inter, h) in stores_e:
    store(x1, x2, y1, y2, "west", h, wm, sm, interior=inter)

# Red spawn: a stone depot at the south end of the street, open to the north.
box(-384, -2240, 0, 384, -1984, 8, "concrete/wall011")
wall_with_openings('x', -2240, 16, -384, 384, 0, 256, [], "concrete/wall011")
wall_with_openings('x', -1984 - 16, 16, -384, 384, 0, 256, [(-96, 96, 8, 176)], "concrete/wall011")
box(-384, -2240, 0, -368, -1984, 256, "concrete/wall011"); box(368, -2240, 0, 384, -1984, 256, "concrete/wall011")
box(-384, -2240, 256, 384, -1984, 272, "concrete/wall011")
light((0, -2112, 200), color="255 230 200", brightness=220)
for i, x in enumerate([-256, -128, 0, 128, 256]):
    ent("info_player_teamspawn", (x, -2112, 16), angles="0 90 0", TeamNum=2)
prop("props_gameplay/resupply_locker.mdl", (-360, -2112, 8), yaw=0, cls="prop_dynamic")

# Blue spawn: down in the cavern, by the tunnel mouth, facing the shaft.
for i, x in enumerate([-640, -512, 512, 640]):
    ent("info_player_teamspawn", (x, CY2 + 160, CAVZ + 16), angles="0 90 0", TeamNum=3)

# ---------------------------------------------------------------- street dressing
# Rails down the middle of the street into the mine.
for y in range(TY0 + 384, TER0 - 576, 256):
    prop("props_mining/track_straight_256.mdl", (0, y + 128, 1), yaw=0)
for y in range(TER0, TL1, 256):
    prop("props_mining/track_straight_256.mdl", (0, y + 128, TZ + 1), yaw=0)
prop("props_trainyard/cart_base_separate.mdl", (0, TER0 + 200, TZ + 6), yaw=90)
# The plaza: water tower, pump, hay, crates, a cart.
prop("props_mining/watertower.mdl", (480, -320, 0))
prop("props_2fort/waterpump001.mdl", (-420, -160, 0), yaw=90)
prop("props_farm/haypile001.mdl", (560, 128, 0), yaw=40)
prop("props_2fort/miningcrate001.mdl", (-520, 64, 0), yaw=15)
prop("props_2fort/miningcrate002.mdl", (-560, -40, 0), yaw=80)
prop("props_trainyard/cart_base_separate.mdl", (-480, -480, 6), yaw=30)
light((0, -192, 260), color="255 200 150", brightness=90)
# The mine terrace: headframe, shack, fences.
prop("props_mining/minehead.mdl", (720, TY1 - 256, TZ), yaw=180)
prop("props_mining/mining_shack001a.mdl", (-760, TY1 - 260, TZ), yaw=0)
for x in range(TX0 + 128, -640, 256):
    prop("props_coalmines/wood_fence_256.mdl", (x + 128, TER0 + 16, TZ), yaw=0)
for x in range(640, TX1 - 128, 256):
    prop("props_coalmines/wood_fence_256.mdl", (x + 128, TER0 + 16, TZ), yaw=0)
for y in range(-2048, 1280, 512):
    prop("props_2fort/telephonepole001.mdl", (-288, y, 0), yaw=90)
    prop("props_2fort/telephonepole001.mdl", (288, y + 256, 0), yaw=90)
    light((0, y + 128, 220), color="255 190 120", brightness=40)
for (x, y, yaw) in [(-1060, -1600, 0), (1180, -800, 30), (-1120, 640, 70), (1200, 900, 10), (-220, 880, 0), (230, 860, 45)]:
    prop("props_farm/wooden_barrel.mdl", (x, y, 0), yaw=yaw)
for (x, y, z, m) in [(-1408, 1280, TZ, "boulder1_large"), (1408, 1200, TZ, "boulder2_large"), (300, 1400, TZ, "boulder_pile2")]:
    prop(f"props_coalmines/{m}.mdl", (x, y, z))

# West: boulders bedded into the foot of the hillside (never standing
# clear of it, which would leave a gap a falling player wedges in), but not
# along the cliff.
boulders = ["boulder1_large", "boulder2_large", "boulder3_large", "boulder_pile1", "boulder_pile2", "boulder5_large", "boulder6_large"]
for i, y in enumerate(range(-2100, 1000, 230)):
    if CLIFF_Y0 - 120 < y < CLIFF_Y1 + 120:
        continue
    prop(f"props_coalmines/{boulders[i % len(boulders)]}.mdl", (west_edge(y) - 24, y, -8), yaw=(i * 67) % 360)
# Rubble at the cliff's foot.
for i, y in enumerate(range(CLIFF_Y0 + 100, CLIFF_Y1 - 60, 310)):
    prop("props_mining/gravel_pile001.mdl" if i % 2 else "props_coalmines/boulder_pile1.mdl", (west_edge(y) + 8, y, 0), yaw=(i * 53) % 360)

def along(fn, t):
    """The yaw (degrees) of a fence laid along an edge line at t, for a line
    given as x(y) (`fn` west/east) or y(x) (south)."""
    return math.degrees(math.atan2(1.0, slope_of(fn, t)))

# East: barbed wire along the canyon rim, following it, broken only where
# the old bridge leaves; danger signs on posts.
BRIDGE_Y0, BRIDGE_Y1 = -704, -576
for i, y in enumerate(range(-2240, 1100, 128)):
    yc = y + 64
    if BRIDGE_Y0 - 64 < yc < BRIDGE_Y1 + 64:
        continue
    prop("props_mining/barbfence001_reference.mdl" if i % 2 == 0 else "props_mining/barbfence002_reference.mdl",
         (east_edge(yc) - 30, yc, 0), yaw=along(east_edge, yc))
for y in (-1500, -300, 700):
    x = east_edge(y + 48) - 64
    box(x, y, 0, x + 8, y + 96, 72, "signs/hazard_yellow001")

# The bridge is out: a trestle bridge's stub reaching over the canyon from
# the plaza, its deck ending in splintered planks, a barricade across its
# near end.
rim = min(east_edge(BRIDGE_Y0), east_edge(BRIDGE_Y1))
box(rim - 64, BRIDGE_Y0, -8, rim + 288, BRIDGE_Y1, 4, WOODFLOOR)
# Planks still nailed at the deck's end hang down into the canyon.
for (ya, yb, reach, drop) in [(BRIDGE_Y0 + 8, BRIDGE_Y0 + 56, 40, -150), (BRIDGE_Y0 + 70, BRIDGE_Y0 + 104, 64, -96)]:
    x0, x1 = rim + 288, rim + 288 + reach
    wedge([(x0, ya, -8), (x0, yb, -8), (x1, yb, drop - 8), (x1, ya, drop - 8)],
          [(x0, ya, 4), (x0, yb, 4), (x1, yb, drop + 4), (x1, ya, drop + 4)], WOODFLOOR)
for x in (rim + 32, rim + 224):
    for y in (BRIDGE_Y0, BRIDGE_Y1 - 16):
        box(x, y, -620, x + 16, y + 16, -8, BEAM)
    box(x, BRIDGE_Y0, -320, x + 16, BRIDGE_Y1, -304, BEAM)
for y in (BRIDGE_Y0, BRIDGE_Y1 - 8):
    for x in range(int(rim), int(rim) + 288, 96):
        box(x, y, 4, x + 8, y + 8, 44, BEAM)
    box(rim, y, 36, rim + 280, y + 8, 44, BEAM)
prop("props_mining/wooden_barrier01.mdl", (rim - 96, (BRIDGE_Y0 + BRIDGE_Y1) / 2, 0), yaw=90)
box(rim - 120, BRIDGE_Y1 + 8, 0, rim - 112, BRIDGE_Y1 + 104, 72, "signs/hazard_yellow001")

# South: a rail yard along the ridge. Its spur runs west into a boarded-up
# adit in a rock outcrop where the ridge meets the west hill; timber
# barricades and barbed wire follow the ridge's foot.
RAIL_Y = -2210
OUT_X = -860                        # the outcrop's face
box(-1300, -2420, -32, OUT_X - 32, -2040, 300, ROCK, top=ROCKTOP)
wedge([(OUT_X - 32, -2420, 300), (OUT_X - 32, -2040, 300), (-1300, -2040, 300), (-1300, -2420, 300)],
      [(OUT_X - 120, -2420, 470), (OUT_X - 120, -2040, 470), (-1300, -2040, 470), (-1300, -2420, 470)], ROCK)
box(-1320, -2440, 300, OUT_X - 8, -2020, ZT - 16, CLIP)
for y in (RAIL_Y - 80, RAIL_Y + 64):
    box(OUT_X - 32, y, 0, OUT_X, y + 16, 192, BEAM)
box(OUT_X - 32, RAIL_Y - 80, 192, OUT_X, RAIL_Y + 80, 208, BEAM)
for z in range(20, 180, 34):
    box(OUT_X - 44, RAIL_Y - 64, z, OUT_X - 36, RAIL_Y + 64, z + 26, "wood/wood_wall006")
box(OUT_X - 28, RAIL_Y - 48, 120, OUT_X - 24, RAIL_Y + 48, 168, "signs/hazard_yellow001")
# The spur ends at a buffer beam short of the spawn depot.
for x in range(OUT_X, -420 - 255, 256):
    prop("props_mining/track_straight_256.mdl", (x + 128, RAIL_Y, 1), yaw=90)
box(-436, RAIL_Y - 64, 0, -420, RAIL_Y + 64, 40, BEAM)
for x in (-700, -540):
    prop("props_trainyard/cart_base_separate.mdl", (x, RAIL_Y, 6), yaw=90)
    prop("props_2fort/miningcrate001.mdl", (x, RAIL_Y, 40), yaw=100)
prop("props_2fort/miningcrate001.mdl", (620, RAIL_Y + 20, 0), yaw=10)
prop("props_2fort/miningcrate002.mdl", (700, RAIL_Y + 30, 0), yaw=75)
for i, x in enumerate(range(-680, 1100, 160)):
    xc = x + 80
    if -420 < xc < 420:
        continue  # the red spawn depot stands here
    yaw = math.degrees(math.atan2(slope_of(south_edge, xc), 1.0))
    prop("props_mining/wooden_barrier01.mdl", (xc, south_edge(xc) + 34, 0), yaw=yaw)
    prop("props_mining/barbedfence_set01.mdl" if i % 3 else "props_mining/barbfence001_reference.mdl",
         (xc, south_edge(xc) - 6, ground(xc, south_edge(xc) - 6)), yaw=yaw)
prop("props_farm/haypile001.mdl", (700, 250, 0), yaw=20)
prop("props_2fort/wagonwheel001.mdl", (330, -1500, 30), yaw=90)
prop("props_mining/sign001.mdl", (-260, TY1 - 64, TZ), yaw=0)
prop("props_mining/billboard001.mdl", (1100, 1350, TZ), yaw=200)
# The terrace's east end stands over the canyon: a rock buttress under it
# and a fence along it.
box(TX1 - 128, TER0, -700, TX1, TY1, -64, ROCK)
for y in range(TER0, TY1 - 128, 256):
    prop("props_coalmines/wood_fence_256.mdl", (TX1 - 24, y + 128, TZ), yaw=90)
# Mine mouth timbering.
for x in (-MX - 16, MX):
    box(x, CY0 - 32, TZ, x + 16, CY0, TZ + MZ + 16, BEAM)
box(-MX - 16, CY0 - 32, TZ + MZ, MX + 16, CY0, TZ + MZ + 32, BEAM)
light((0, CY0 - 96, TZ + 200), color="255 190 120", brightness=120)

# Townsfolk: TF2 mercenaries standing about the street and porches.
people = [("engineer", (-200, -1400, 12), 0), ("heavy", (250, -900, 0), 200), ("scout", (-300, -240, 0), 300),
          ("medic", (-420, -944, 12), 0), ("sniper", (620, -160, 0), 180), ("demo", (120, 600, 0), 250),
          ("soldier", (-120, TER0 + 160, TZ), 90), ("pyro", (0, CY2 + 320, CAVZ), 90), ("spy", (-640, -100, 12), 10),
          ("heavy", (-600, 200, 0), 300), ("engineer", (600, TER0 + 300, TZ), 200), ("demo", (1000, GY0 + 192, CAVZ), 180)]
for (cls, o, yaw) in people:
    ent("prop_dynamic", o, model=f"models/player/{cls}.mdl", angles=f"0 {yaw} 0", DefaultAnim="stand_MELEE", solid=0, skin=0)

# ---------------------------------------------------------------- mine dressing
for y in range(TL0, RY0, 128):
    prop("props_mining/tunnel_truss01.mdl", (0, y + 64, TZ), yaw=90)
for y in range(RY0 + 64, RY1, 128):
    z = TZ + (y - RY0) / (RY1 - RY0) * (CAVZ - TZ)
    light((0, y, z + 180), color="255 180 110", brightness=70)
for (x, y) in [(-640, CY2 + 320), (640, CY2 + 320), (-640, CY2 + 768), (640, CY2 + 768), (0, CY2 + 256)]:
    light((x, y, CTOP - 64), color="255 185 120", brightness=260)
    prop("props_mining/industrial_ceiling_lamp.mdl", (x, y, CTOP - 8))
for (x, y, m, yaw) in [(-700, CY2 + 512, "props_mining/gravel_pile001.mdl", 0), (700, CY2 + 600, "props_mining/gravel_pile001.mdl", 90),
                       (-500, CY3 - 120, "props_2fort/miningcrate001.mdl", 10), (-420, CY3 - 120, "props_2fort/miningcrate002.mdl", 0),
                       (520, CY3 - 140, "props_trainyard/cart_base_separate.mdl", 90), (300, CY2 + 200, "props_farm/wooden_barrel.mdl", 0),
                       (-300, CY2 + 220, "props_2fort/oildrum.mdl", 0)]:
    prop(m.replace("models/", ""), (x, y, CAVZ), yaw=yaw)
for x in range(CX0 + 128, CX1, 384):
    prop("props_mining/support_wall001a.mdl", (x, CY2 + 16, CAVZ), yaw=90)
for y in range(CY2 + 64, SY0 - 64, 256):
    prop("props_mining/track_straight_256.mdl", (0, y + 128, CAVZ + 1), yaw=0)
light((0, (SY0 + SY1) / 2, PITZ + 64), color="255 120 60", brightness=300)  # glow from below
light((0, (SY0 + SY1) / 2, CAVZ - 300), color="255 150 90", brightness=150)
prop("props_gameplay/elevator_well_platform.mdl", (0, (SY0 + SY1) / 2, CAVZ - 160), cls="prop_dynamic")
# The side gallery: ore, carts, a lamp.
light((CX1 + 240, (GY0 + GY1) / 2, CAVZ + 160), color="255 185 120", brightness=200)
for (dx, dy, m, yaw) in [(120, 80, "props_mining/gravel_pile001.mdl", 0), (300, 260, "props_trainyard/cart_base_separate.mdl", 0),
                          (360, 80, "props_2fort/miningcrate001.mdl", 30), (140, 300, "props_mining/ingot001.mdl", 0)]:
    prop(m.replace("models/", ""), (CX1 + 32 + dx, GY0 + dy, CAVZ), yaw=yaw)

# ---------------------------------------------------------------- write
world = (f'world\n{{\n\t"id" "{nid()}"\n\t"mapversion" "1"\n\t"classname" "worldspawn"\n'
         f'\t"skyname" "sky_badlands_01"\n\t"detailmaterial" "detail/detailsprites"\n\t"detailvbsp" "detail.vbsp"\n'
         f'\t"maxpropscreenwidth" "-1"\n' + "".join(solids) + "}\n")
ents = []
for e in entities:
    solid = e.pop("__solid", "")
    body = "".join(f'\t"{k}" "{v}"\n' for k, v in e.items())
    ents.append(f'entity\n{{\n\t"id" "{nid()}"\n{body}{solid}}}\n')
env = [
    {"classname": "light_environment", "origin": "0 0 512", "angles": "-50 125 0", "pitch": "-50",
     "_light": "255 226 190 420", "_ambient": "140 150 185 70", "_lightHDR": "-1 -1 -1 1", "_ambientHDR": "-1 -1 -1 1"},
    {"classname": "env_sun", "origin": "0 0 480", "angles": "-50 125 0", "pitch": "-50", "use_angles": "1"},
    {"classname": "tf_gamerules", "origin": "0 -2000 64", "targetname": "tf_gamerules"},
]
for e in env:
    body = "".join(f'\t"{k}" "{v}"\n' for k, v in e.items())
    ents.append(f'entity\n{{\n\t"id" "{nid()}"\n{body}}}\n')
open(out_path, "w").write('versioninfo\n{\n\t"editorversion" "400"\n\t"mapversion" "1"\n\t"formatversion" "100"\n\t"prefab" "0"\n}\n'
                          + world + "".join(ents))
print(f"wrote {out_path}: {len(solids)} brushes, {len(entities) + len(env)} entities")
