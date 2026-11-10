# The `.nav3d` level navigation file

`ssmap link` writes `<map>.nav3d` beside every linked `<map>.bsp`, and
`ssmap nav3d` and `ssmap all` beside any compiled map (section 17.6): a 3D
navigation of the level's free space for agents that walk, climb, jump,
swim and fly, of any box size. This document is the file's specification,
version **2**. It is written so a reader can be built from it alone, in any
language; the C# reader in `SourceSharp.MapFormats` (`Nav3dReader`) is one
such reader, and the facts check the two against each other.

Contents:

1. [What the file describes](#1-what-the-file-describes)
2. [Coordinates and conventions](#2-coordinates-and-conventions)
3. [Layout](#3-layout): envelope, header, section directory
4. [Sections](#4-sections): every record, byte by byte
5. [Clearance records](#5-clearance-records): one record answers every agent size, and why it is exact
6. [Columns, leaves and point lookup](#6-columns-leaves-and-point-lookup)
7. [Queries: fit, standing, head room, steps](#7-queries-fit-standing-head-room-steps)
8. [What a reader derives at load](#8-what-a-reader-derives-at-load): neighbours, components, points' and obstacles' leaves
9. [Traversal: steps, jumps, ladders, water, costs](#9-traversal-steps-jumps-ladders-water-costs)
10. [Dynamic obstacles](#10-dynamic-obstacles): doors, movers, breakables, props at run time
11. [Points of interest, the spawn and arrivals](#11-points-of-interest-the-spawn-and-arrivals)
12. [Ids: tying the navigation to its map](#12-ids-tying-the-navigation-to-its-map)
13. [How a reader walks it](#13-how-a-reader-walks-it) (C++-style walk-through)
14. [Using the reader from the mod (C#)](#14-using-the-reader-from-the-mod-c)
15. [A worked example](#15-a-worked-example), byte by byte
16. [Versioning](#16-versioning)
17. [Where the data comes from](#17-where-the-data-comes-from): configuration, the room pack, seams, the map-first link, a compiled map
18. [Measurements and the choices they made](#18-measurements-and-the-choices-they-made)

---

## 1. What the file describes

A level is a grid of **cells**, one room per cell (some cells empty).
Each cell is split into **voxels**, `cellVoxels` along each edge (16 by
default: a 256-unit cell, 16-unit voxels). A cell is a cube unless its room
has a height of its own (the rooms design, 17.6): then its columns run up
that room's height in voxels instead, which only a **version 3** file
records (`CHGT`, section 4); every floor stands at z = 0. A room larger
than one cell (the rooms design, 17.8) covers several cells: each holds its
own part of the room's grid, its room's name and its turn, and the door
bits of the doors on its own edges, so a reader's point lookup is a cell's
whatever the room; the file's layout and version do not change for it.
Each voxel
**column** of a placed cell lists its free space as **leaves**: runs of
voxels, bottom to top, each run a stretch of the column where everything
the file knows is the same. Solid is simply not listed.

Every leaf carries two **clearance records**, one for each **clip class**
(player clip or monster clip; everything else solid stops both). A record
answers, for **every** axis-aligned agent box at once, whether the agent fits
in a voxel of the leaf: the file does not depend on which agents a game
uses. The **presets** the file names (`standing`, `flyer`, ...) are a
convenience recorded for the runtime; the grid is the same with none.

A leaf also carries its **floor** per class (the height of the solid right
under its bottom voxel, and whether it is walkable), its **water** and
**ladder** flags, and a **cost** multiplier.

Beside the grid the file holds the level's **doors**, **points of
interest**, **dynamic obstacles** (doors, movers, breakables, props: open
space in the grid, tagged so the runtime can block them), **jump links**
between floors, and the few **overhanging brushes** whose shape a record's
corners cannot state.

It does **not** hold adjacency or components: they are a pure function of
the columns, so every reader derives them at load, deterministically
(section 8). Version 1 stored them, at about half its size.

**Free means the agent fits.** An agent fits in a voxel when its box, with
its origin *anywhere* in the voxel, overlaps no solid of its class. That is
exact, not sampled: facts compare it voxel for voxel with a direct box sweep
of the compiled map (section 5.4).

The file does not choose how agents move. A walker stands on walkable
floors and steps by the step height; a jumper also takes jump links; a
climber uses ladder leaves; a flyer goes anywhere it fits.

## 2. Coordinates and conventions

- **Units** are Source units (inches). **Z is up.** Rotation is
  counter-clockwise seen from above.
- **Endianness**: every multi-byte value is **little-endian**. Floats are
  IEEE 754 binary32, `±∞` allowed where stated. Ids are 16 bytes in RFC 9562
  (big-endian, "network") order.
- **The grid**: `columns` cells west to east (+x), `rows` south to north
  (+y). Cell `(column, row)` has index `row × columns + column` and spans
  `origin + (column × cellSize, row × cellSize, 0)` to that plus `cellSize`
  on every axis; in a version 3 file, to that plus `cellSize` across and
  `CHGT[cell] × voxelSize` up. The linker's origin is `(0, 0, 0)`.
- **Voxels**: `voxelSize = cellSize / cellVoxels`, `cellVoxels` at most 128;
  a cell's height (`CHGT`, version 3) at most 255, the most a leaf's 8-bit
  `zLo` and `height` reach.
  Voxel `(x, y, z)` of a cell spans `cellCorner + (x, y, z) × voxelSize` to
  that plus `voxelSize`; a voxel's **top** is `origin.z + (z + 1) × voxelSize`.
- **Half-open**: a point on a voxel boundary belongs to the voxel above it on
  each axis: `x = floor((p.x − origin.x) / voxelSize)`.
- **An agent** is a box of width `w` (and depth `w`: square seen from above),
  height `h`, standing on its origin: `(−w/2, −w/2, 0)` to `(w/2, w/2, h)`,
  as a Source player's or NPC's hull is. `r = w/2` is its **half-width**.
- **Directions**: 0 east (+x), 1 north (+y), 2 west (−x), 3 south (−y),
  4 up, 5 down. Yaw is degrees counter-clockwise from +x, `[0, 360)`.
- **Strings** are offsets into the string table; `0xFFFFFFFF` means none.
- **ε** is `0.001` units, the tolerance every overlap test uses: a box must
  reach more than ε into a solid to overlap it.

## 3. Layout

```
envelope (24 bytes, never compressed)
stored image (raw, or compressed by the envelope's codec)
    header (headerBytes, 160 in version 2)
    section directory (sectionCount × 16 bytes)
    sections (each starting on a 4-byte boundary, zero padding between)
```

### 3.1 Envelope

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `char[8]` | magic `SSNAV3D\0` (`53 53 4E 41 56 33 44 00`) |
| 8 | `int32` | version: **2**, or **3** for a level whose cells have heights of their own (section 16) |
| 12 | `uint8` | codec: 0 none, 1 Deflate (raw RFC 1951), 2 Brotli (RFC 7932) |
| 13 | `uint8[3]` | zero |
| 16 | `int32` | `imageLength`: the image's length after decoding |
| 20 | `int32` | `storedLength`: bytes that follow the envelope; the file is exactly `24 + storedLength` bytes |

With codec 0 the stored bytes are the image and a reader may use them in
place. Otherwise the reader decodes them into a buffer of `imageLength`
bytes, once, at load. `ssmap link` writes **Brotli** (quality 5) by
default (section 18). Every offset below is from the start of the
**image**.

### 3.2 Header (image offset 0)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `int32` | `headerBytes`: 160 in versions 2 and 3. The directory starts here. |
| 4 | `int32` | `sectionCount` |
| 8 | `int32` | `presetCount`, 0 to 32 |
| 12 | `float32` | `cellSize` |
| 16 | `float32` | `voxelSize` |
| 20 | `int32` | `cellVoxels`, 1 to 128 |
| 24 | `int32` | `columns` (cells west to east) |
| 28 | `int32` | `rows` (cells south to north) |
| 32 | `float32[3]` | `origin` |
| 44 | `float32` | `floorNormalZ`: a floor is walkable when its normal z is at least this (0.7 by default) |
| 48 | `float32` | `stepHeight` (18) |
| 52 | `float32` | `jumpHeight` (56) |
| 56 | `float32` | `jumpDistance` (100) |
| 60 | `int32` | `poiCount` |
| 64 | `int32` | `doorCount` |
| 68 | `int32` | `spawnPoi`, or −1 |
| 72 | `int32` | `upArrivalPoi`, or −1 |
| 76 | `int32` | `downArrivalPoi`, or −1 |
| 80 | `int32` | `columnCount`: placed cells × `cellVoxels²` |
| 84 | `int32` | `leafCount` |
| 88 | `int32` | `obstacleCount` |
| 92 | `int32` | `brushCount` |
| 96 | `int32` | `jumpCount` |
| 100 | `int32` | `clearanceBytes`: the `CLRS` section's length |
| 104 | `byte[16]` | `levelId` (section 12) |
| 120 | `byte[16]` | `packId` (section 12) |
| 136 | `byte[24]` | zero, reserved |

### 3.3 Section directory

`sectionCount` entries of 16 bytes, at image offset `headerBytes`:

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `char[4]` | tag, ASCII |
| 4 | `uint32` | index: `0xFFFFFFFF` (every section of version 2 belongs to the level) |
| 8 | `uint32` | offset of the section from the image's start; a multiple of 4 |
| 12 | `uint32` | length in bytes |

A reader looks sections up by tag and **ignores tags it does not know**.

## 4. Sections

| Tag | Record | Count |
| --- | --- | --- |
| `STRS` | bytes | string table: NUL-terminated UTF-8; offset 0 is the empty string |
| `AGNT` | 16 bytes | `presetCount` |
| `CELL` | 8 bytes | `columns × rows` |
| `DOOR` | 16 bytes | `doorCount` |
| `POIS` | 48 bytes | `poiCount` |
| `ROOT` | `int32` | `columns × rows`: each cell's first voxel column, −1 for an empty cell |
| `COLS` | `uint32` | `columnCount + 1`: column `j`'s leaves are `[COLS[j], COLS[j+1])` |
| `LEAF` | 24 bytes | `leafCount` |
| `CLRS` | bytes | the clearance records (section 5), each 4-byte aligned |
| `DYNO` | 48 bytes | `obstacleCount` |
| `BRSI` | `uint32` | `brushCount + 1`: brush `b`'s planes are `[BRSI[b], BRSI[b+1])` |
| `BRSP` | 16 bytes | the overhanging brushes' planes: `float32` normal x, y, z, distance |
| `JUMP` | 16 bytes | `jumpCount` |
| `CHGT` | `int32` | `columns × rows`, **version 3 only**: each cell's height in voxels, 1 to 255 for a placed cell, 0 for an empty one |
| `BSPB` | 40 bytes | one, **optional** (versions 2 and 3): the compiled map the navigation was built from (section 12) |

**`CHGT`** (version 3, written after `JUMP`) gives each cell its own
height in voxels: how far up its columns run from the floor. A room taller
than its cell has more voxels up than across, a low room fewer. A version 2
file has no `CHGT`, and every placed cell is `cellVoxels` tall; the linker
writes version 2, byte for byte as before heights, for a level whose rooms
are all cubes. A version 3 reader requires `CHGT` and holds every leaf of a
cell's columns under its height.

**`BSPB`** (optional, written last) binds a navigation built from a
compiled map (`ssmap nav3d`, `ssmap all`; section 17.6) to that map's
exact bytes. A file without it (every linked level, and every file
written before the section existed) is bound to no map; a reader that
does not know the tag skips it (section 3.3), which is why it changes no
version.

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | the map's checksum: the CRC-32 of every lump but the entities, in lump order, as the engine computes it (`BspMapChecksum`) |
| 4 | `uint32` | zero |
| 8 | `byte[32]` | the SHA-256 of the whole `.bsp` file |

**`AGNT`** (per preset)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | name (string) |
| 4 | `float32` | width |
| 8 | `float32` | height |
| 12 | `uint8` | clip class: 0 player, 1 NPC |
| 13 | `uint8[3]` | zero |

**`CELL`** (per cell, `row × columns + column`)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | room name (string), `0xFFFFFFFF` for an empty cell |
| 4 | `uint8` | rotation: quarter turns counter-clockwise |
| 5 | `uint8` | role: 0 none, 1 up, 2 down |
| 6 | `uint8` | joined doors: bit d set for direction d |
| 7 | `uint8` | capped doors: bit d set for direction d |

**`DOOR`** (per socket of a placed room; a joined door appears once for each side)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | cell |
| 4 | `uint8` | direction it faces (0 E, 1 N, 2 W, 3 S) |
| 5 | `uint8` | 1 joined, 0 capped |
| 6 | `uint16` | zero |
| 8 | `uint32` | socket name (string) |
| 12 | `int32` | the facing door's index when joined, else −1 |

**`POIS`** (per point)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `float32[3]` | position, level coordinates |
| 12 | `float32` | yaw, degrees |
| 16 | `float32` | radius, 0 for none |
| 20 | `uint32` | type (string) |
| 24 | `uint32` | tags (string, comma-separated as authored) |
| 28 | `uint32` | name (string, room-local names resolved), or `0xFFFFFFFF` |
| 32 | `uint32` | cell of the room it belongs to |
| 36 | `uint32` | preset mask: bit p set when the point applies to preset p |
| 40 | `int32` | door record for a door point, else −1 |
| 44 | `uint16` | flags: 1 has facing, 2 door point, 4 joined (door point on a joined door), 8 arrival |
| 46 | `uint8` | role of its room: 0 none, 1 up, 2 down |
| 47 | `uint8` | zero |

**`LEAF`** (per leaf; a column's leaves are low to high and never overlap)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint8` | `zLo`: the run's bottom voxel, in the cell |
| 1 | `uint8` | `height`: voxels in the run, at least 1 |
| 2 | `uint16` | flags (section 9): 1 water, 2 ladder, 4 grounded (player), 8 grounded (NPC), 16 walkable (player), 32 walkable (NPC) |
| 4 | `uint16` | cost, 8.8 fixed point (256 = 1.0) |
| 6 | `uint16` | zero |
| 8 | `uint32` | player class's clearance record: byte offset into `CLRS` |
| 12 | `uint32` | NPC class's clearance record |
| 16 | `float32` | player floor height (when grounded for the player class, else 0) |
| 20 | `float32` | NPC floor height |

Many leaves share a record: the two offsets are often equal, and a room has
a few hundred distinct records against thousands of leaves.

**`DYNO`** (per dynamic obstacle, section 10)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | name: its `targetname` with room-local names resolved, or `0xFFFFFFFF` |
| 4 | `uint32` | classname (string) |
| 8 | `uint32` | cell of the room it belongs to |
| 12 | `int32` | its `hammerid` in the room, or −1 |
| 16 | `uint8` | kind: 1 door, 2 mover, 3 toggle, 4 breakable, 5 physics, 6 prop |
| 17 | `uint8[3]` | zero |
| 20 | `float32[3]` | bounds' low corner, level coordinates |
| 32 | `float32[3]` | bounds' high corner |
| 44 | `uint32` | zero |

**`BRSP`**: a brush is the intersection of the half-spaces
`n · p ≤ d` of its planes.

**`JUMP`** (per jump link, section 9.2)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | leaf A (the lower-numbered) |
| 4 | `uint32` | leaf B |
| 8 | `float32` | rise: B's floor minus A's |
| 12 | `uint8` | class mask: bit 0 player, bit 1 NPC |
| 13 | `uint8` | direction from A to B (0-3) |
| 14 | `uint8` | columns apart: 1 for a ledge, more across a gap |
| 15 | `uint8` | zero |

## 5. Clearance records

### 5.1 What a record answers

An agent of half-width `r` and height `h` fits in a voxel spanning
`[x0, x1] × [y0, y1] × [z0, z1]` when the **swept box**
`[x0 − r, x1 + r] × [y0 − r, y1 + r] × [z0, z1 + h]` overlaps no solid of
its class: that is the box with its origin anywhere in the voxel. A record
answers this for every `(r, h)`.

### 5.2 Layout

A record is a multiple of four bytes:

| Bytes | Field |
| --- | --- |
| 2 | `uint16` static corner count `S` |
| 2 | `uint16` dynamic corner count `D` |
| 2 | `uint16` brush count `B` |
| 2 | zero |
| 8 × S | static corners: `float32` width threshold `R`, `float32` top threshold `T`; `R` strictly rising, `T` strictly falling |
| 12 × D | dynamic corners: `uint32` obstacle, `float32` `R`, `float32` `T`; by obstacle, then `R` |
| 4 × B | overhanging brushes: `uint32` index into `BRSI`, ascending |

A **corner** `(R, T)` blocks an agent in a voxel of top `z1` exactly when

```
r > R + ε   and   z1 + h > T + ε
```

`R = −∞` means "any width", `T = −∞` "any height". The record that blocks
everything, even a point, is the single corner `(−∞, −∞)`; its 16 bytes are
`01 00 00 00 00 00 00 00 00 00 80 FF 00 00 80 FF`. A dynamic corner blocks
only while its obstacle does (section 10). An overhanging brush is tested
directly (5.3).

### 5.3 Why a staircase of corners is exact

Take one solid brush of the agent's class. If it lies wholly below the
voxel it never blocks (the box starts at the voxel's floor). Otherwise:

- **Axis-aligned brush.** The swept box overlaps it exactly when the box's
  footprint, widened by `r`, reaches it on both horizontal axes, and the
  box's top rises past the brush's bottom. The first condition is
  `r > g`, where `g` is the **Chebyshev gap** from the voxel's footprint to
  the brush's: `max(x0 − maxX, minX − x1, y0 − maxY, minY − y1)`. The second
  is `z1 + h > minZ`. So the brush is the corner `(g, minZ)`, with `ε` on
  both sides as every overlap test has it.
- **Not axis-aligned, no downward sloped face** (a ramp, an angled wall).
  Its bottom is flat or it rests on the ground, so it grows no wider going
  up: once the box's top reaches the brush, whether they overlap depends
  on the box's width alone. The separating-axis test gives the exact
  width `R` at which the widened box first overlaps (`NavBrush.
  GrowthThreshold`), and the brush is the corner `(R, minZ)`.
- **A brush with a sloped face turned downward** (the underside of a stair,
  an overhang): under it, a wider agent has less head room. No single
  corner states that slanted edge, so the record lists the brush by index,
  and the reader runs the separating-axis test on the swept box itself. The
  builder keeps every voxel such a brush reaches a leaf of its own.

A voxel is blocked iff some brush blocks it, so it is blocked iff some
corner blocks. A corner **dominates** another when it is no wider and no
higher (`R₁ ≤ R₂`, `T₁ ≤ T₂`): whatever the second blocks, the first does.
Dropping dominated corners loses nothing, and the rest form a staircase,
widths rising and tops falling. Nothing is approximated, so the record's
answer equals the direct box test for every width and height.

Two canonical rules make equal clearance give equal bytes, so records can
be shared: a corner whose width is below zero (`R + ε < 0`: it blocks even a
point horizontally) is stored as `R = −∞`, and one whose top the voxel's
floor already exceeds (`z0 > T + ε`: it blocks at any height) as `T = −∞`.
Every threshold is rounded **down** to `float32`, so a rounding can only
block an agent a builder would have let through by less than a float's
last bit, never the reverse.

### 5.4 The evidence

- **Direct sweep.** On every one of the 16 default 3x3 sample levels, for
  the two presets and three sizes no preset names (24 × 150 NPC, 90 × 40
  NPC, 6 × 10 player), every voxel of every placed room fits exactly when a
  direct box sweep against the flattened level's compiled world brushes
  says it does (`Rooms3x3NavTests.EveryAgentSizeFitsExactlyWhere…`). The
  unit facts do the same over random boxes against ramps, overhangs, clips
  and pillars (`NavClearanceBuilderTests`).
- **Flatten equivalence.** On the same 16 levels, the stitched file's
  columns equal, run for run and record for record, the grid built straight
  from the flattened level's whole-map compile: bounds, flags, costs,
  floors, both classes' corners, dynamic corners and overhang brushes.

### 5.5 Clip classes

Player clip stops players and not NPCs, monster clip the reverse, and all
else solid stops both. So there are exactly two worlds an axis-aligned
agent can live in, and a leaf has one record for each (often the same
record). Contents masks: player `0x0201400B`, NPC `0x0202400B`. A water or
slime brush (`0x30`) and a ladder brush (`0x20000000`) are flags, never
solid.

## 6. Columns, leaves and point lookup

A placed cell's columns are stored as one block, x fastest, starting at
`ROOT[cell]`: column `(x, y)` of the cell is `ROOT[cell] + y × cellVoxels + x`.

A leaf is a run of voxels with one key: the same two records, flags (floor
flags aside, which describe the run's bottom) and cost. Every voxel of the
run fits the same set of agents **from the bottom up**: the thresholds are
absolute altitudes, so a higher voxel is only more constrained. An agent's
room in a leaf is therefore a prefix of the run (7.2).

**Point lookup** (`FindLeaf`): compute the level voxel of the point; the
cell is `(X / cellVoxels, Y / cellVoxels)`; outside the grid or in an empty
cell there is no leaf, and neither is there at or above the cell's height
(`cellVoxels`, or `CHGT[cell]` in version 3; the grid reaches up to its
tallest cell). Scan the column's leaves for the one whose
`[zLo, zLo + height)` holds `z`; none means solid there.

## 7. Queries: fit, standing, head room, steps

### 7.1 Passable

`Passable(leaf, z, w, h, class, blocking)`: the voxel `z` of the leaf, its
top `z1 = origin.z + (z + 1) × voxelSize`, `r = w/2`. Not passable when any
static corner blocks, or any dynamic corner whose obstacle is `blocking`
blocks, or any listed brush overlaps the swept box.

### 7.2 Fit top

The highest voxel of a leaf an agent fits in; it fits in every voxel from
`zLo` to there. For records without brushes, the lowest `T` among the
corners wider than the agent bounds it: `z1 + h ≤ T + ε`. A record with a
brush is a one-voxel leaf, tested directly.

### 7.3 Standing

An agent **stands** in a leaf when it fits in the leaf's bottom voxel and
cannot sink: either the leaf is **grounded** for its class (solid right
under the bottom voxel; then the floor must be **walkable**), or the leaf
below does not fit it (it rests on the rim of a hole too narrow or low for
it, which counts as walkable). Only a run's bottom voxel can be stood in.

### 7.4 Head room and width room

`VerticalClearance(leaf, z, w)`: the most height an agent of width `w` has
in the voxel; `HorizontalClearance(leaf, z, h)`: the most width an agent
of height `h` has. Both read the staircase, and the brush tests' growth
thresholds, so they are exact too.

### 7.5 Steps

The **floor** of a grounded leaf is the exact height of the solid under
its bottom voxel for that class: the highest point of the brushes the
voxel's column rests on (a slope's highest point under the voxel). The
step from leaf A to B is `floor(B) − floor(A)`; a walker takes it when it
is at most `stepHeight` (18 by default: exactly 18 walks, 18.001 does not),
and down any drop. More, and it needs a jump link.

### 7.6 Fit at the voxel's centre

A fit (7.1) holds for an origin **anywhere** in the voxel, so an agent of
half-width `r` needs `r` clear on every side of the whole voxel: a 32-wide
player needs 48 units across at 16-unit voxels, and 72 units of head room
above the voxel's **top**. A passage 40 wide, a ladder close to its wall or
a 48-high crawlway is closed to it. The same records answer an agent whose
origin is at the voxel's **horizontal centre** and at a chosen height `z`
(its feet), with no change to the file:

```
fits at centre (r, h, z) = not CornersBlock(record, r - voxelSize/2, h, voxelTop := z)
                           and no listed overhanging brush overlaps
                           (centre ± r, z .. z + h)
```

A corner's width is how far the voxel's footprint, grown the same on every
side, can grow before it meets the solid; a box of half-width `r` centred
on the voxel is that footprint grown by `r − voxelSize/2`, so the threshold
is exact for any solid. A solid that reaches into the footprint is stored
with no width (5.2) and so blocks such an agent whenever it reaches the
solid's bottom: the answer can be no where the truth is yes, never the
reverse. The C# reader answers it as `FitsAtCentre` (any height),
`PassableAtCentre` (feet on the voxel's bottom) and `StandableAtCentre`
(feet on the leaf's floor). A 32-wide player then needs 32 across and 72
above its feet.

`Nav3dWalk.Components` derives, from these answers alone, which leaves a
walking agent reaches: places to stand (walkable floors it fits on standing
or crouched), places to climb (ladder leaves it fits in), and moves between
them (a step to a column beside within `stepHeight`, the file's jump links,
up and down a ladder's column, and on and off a ladder to floors its box
reaches). A step also joins two places up to `ceil(width / 2 / voxelSize) + 1`
columns apart along an axis, floors within `stepHeight`, when the agent fits
crouched at the centre of every column between with its feet at the higher
floor: in front of a sheer step the box stands no nearer the face than its
half-width, so the place before the step and the step's top are two columns
apart for a 32-wide player at 16-unit voxels, and the box crosses the
step's foot at the top's height, as the game's step does. It is a reference for a game's own graph and what the facts
check maps with.

## 8. What a reader derives at load

All of it in a fixed order, so every load of a file gives the same arrays.

**Neighbours.** Two leaves neighbour when they are in the same column and
touch (`Up`/`Down`: one's top voxel is right under the other's bottom), or
in columns side by side (east, north, west, south; across a cell face
too) and their voxel runs overlap in z. A neighbour in another cell is
**through a door**: the kit's walls leave no other opening between cells.
Per leaf, the list is east, north, west, south (each low to high), up,
down. The relation is agent-independent; whether an agent can make a
step is its fit in both leaves: two leaves connect for an agent when its
fitting prefixes overlap (sideways) or the lower one fits right to its top
(vertically).

**Components**, per preset: the connected components of that relation
over the leaves the preset fits in, numbered in order of first leaf; −1
for a leaf it fits nowhere in. Every obstacle open. A fact checks, on all
16 sample levels, that these partitions equal version 1's: the direct
sweep's free voxels joined face to face.

**A point's leaf**: the leaf of its own room's cell holding its position,
the voxel clamped into that cell (a door point stands on the cell face;
on an east or north face the half-open rule would otherwise put it in
the neighbour). −1 when that voxel is solid.

**An obstacle's leaves**: the leaves whose records name it.

**A leaf's jump links**: the `JUMP` records naming it at either end.

## 9. Traversal: steps, jumps, ladders, water, costs

### 9.1 Floors and flags

Per leaf and class: **grounded** when solid of the class is right under
the bottom voxel; **walkable** when that surface's normal z is at least
`floorNormalZ`. **Water** when the leaf's voxels overlap a water or slime
brush; **ladder** when they overlap a `CONTENTS_LADDER` brush, an
`info_ladder`'s box (`mins`/`maxs` or the `mins.x` ... keys) or a
`func_useableladder`'s climb (the box spanning `point0` and `point1`,
widened 16 units, a player's half-width). Water and ladders are free
space.

**Cost** is `nav_cost_water` (2) for water, `nav_cost_ladder` (1.5) for a
ladder, their product for both, 1 otherwise; stored ×256.

### 9.2 Jump links

For each class, every walkable floor (a leaf grounded and walkable) is
paired with the walkable floors up to `floor(jumpDistance / voxelSize)`
columns away along each axis when:

- their floors differ by at most `jumpHeight` (56: a crouch jump);
- a walk does not already join them (adjacent columns with a rise within
  the step height);
- every column between is open at the higher floor's standing voxel;
- no column between has a floor within the step height of the range the
  jump spans (a walker would use that floor instead).

A link is bidirectional: up by jumping, down by dropping. Whether a given
agent clears the arc is its own clearance's answer at run time. Links of
both classes with the same floors are one record with both mask bits.
Defaults: 100 units reach (a Half-Life 2 player at 190 u/s stays about
0.53 s in the air on its 21-unit jump under gravity 600).

## 10. Dynamic obstacles

Doors (`func_door`, `func_door_rotating`, `prop_door_rotating`), movers
(`func_movelinear`, `func_train`, `func_tracktrain`, `func_rotating`,
`func_plat`, `func_platrot`), toggles (`func_brush`, `func_wall_toggle`),
breakables (`func_breakable`, `func_breakable_surf`), physics
(`func_physbox*`, `prop_physics*`) and props (`prop_dynamic*`) are **not
solid** in the grid. A brush entity's own brushes are its solids, where
the game places them: the compile stores the brushes of an entity with an
`origin` (a rotating door's origin brush gives it one) relative to it, so
they are moved by it; a prop's model hull (from the game's content),
turned by its angles, is its box; a prop whose model the content lacks is
left out with a warning.

Each becomes a `DYNO` record, and each voxel it could block carries its
**dynamic corners** in the record (a corner a static one dominates is
dropped: it can never be the reason). The name is the entity's
`targetname` resolved the way the link names entities (`cxry_gate` in cell
(2, 0) is `c2r0_gate`, turned with the room), so the runtime finds the
entity by the same name the map gives it. An unnamed one is found by cell
and `hammerid`.

**At run time**: keep a `bool` per obstacle (`blocking[o]`: the door is
closed, the breakable intact, the prop where it was placed), and pass it
to the queries. `ObstacleLeaves(o)` names the leaves whose answer can
change when `o` does, so a planner invalidates only those. A
`func_door` in a socket's doorway is tagged when the door is joined and
gone with the doorway when the level caps it.

## 11. Points of interest, the spawn and arrivals

- **Authored**: `info_poi` point entities in a room (17.2). Their position
  and yaw turn with the room; a room-local name (`cxry_…`) is resolved to
  the cell (`c<column>r<row>_…`), as the link resolves entity names.
- **Door points**: one per door record, at the door's centre on the cell
  face, on the doorway's floor, facing out of the room (type `door`, flag
  2, flag 4 when joined). It applies to every preset (the mask has every
  preset's bit): whether an agent fits through is its clearance's answer.
  A capped doorway is solid, so its point's leaf is −1.

**A point in a capped doorway is refused.** An authored point that stands
in a socket's doorway (the plug's box) is fine while the level joins the
door, but where the level caps it the point would be inside the plug. The
link refuses before writing the map:
`room "hall" at cell (1, 0): info_poi 42 "cxry_sentry" (vantage) stands in
the doorway of socket "east", which the level caps; a capped doorway is
filled by its plug, so the point would be inside the wall.`

**Arrivals.** An `arrival` point is where a player appears arriving from
another level: it has a facing, applies to the player presets, and stands
on a walkable floor where they fit. `upArrivalPoi` and `downArrivalPoi`
index the first arrival in an up room and a down room, or −1.
**The spawn**, `spawnPoi`, is the up room's arrival.

## 12. Ids: tying the navigation to its map

The link writes one **level id** into the map's worldspawn key
`ss_level_id` and the header's `levelId`; the mod checks they are equal
(`MatchesMap`) before trusting the navigation. `ss_pack_id` / `packId` name
the room compile. Both are RFC 9562 version 8 UUIDs from a SHA-256 of their
inputs (pack: library bytes, room options, navigation settings, tool
version; level: pack id, level file bytes, link options). The keys are
written only when the link writes a `.nav3d`: a link without navigation
writes byte for byte the map a link wrote before navigation existed.

**A map compiled any other way** (a stock map such as `dm_lockdown`, or a
map from `ssmap all`) has no `ss_level_id`, and its navigation is built
from the compiled file afterwards (section 17.6). That navigation's
`levelId` and `packId` are zero, and its `BSPB` section names the map
instead: the map checksum (the CRC-32 the engine computes, every lump but
the entities) and the SHA-256 of the whole file. The mod checks the map
it loaded with `MatchesBsp(bspFileBytes)`, true only when both are equal.
Both, because the engine's checksum leaves the entities out while the
navigation reads doors, ladders and props from them: an entity-only edit
keeps the checksum and changes the hash. `Nav3dBspBinding.Of(bytes)` in
`SourceSharp.MapFormats` computes the pair, so the tools and the mod
compute it the same way.

## 13. How a reader walks it

```c++
// Load.
struct Envelope { char magic[8]; int32_t version; uint8_t codec, pad[3]; int32_t imageLength, storedLength; };
const Envelope* e = (const Envelope*)file;
check(memcmp(e->magic, "SSNAV3D\0", 8) == 0 && (e->version == 2 || e->version == 3) && 24 + e->storedLength == fileSize);
const uint8_t* image = file + 24;
if (e->codec == 1) image = inflateRaw(file + 24, e->storedLength, e->imageLength);  // zlib windowBits -15
if (e->codec == 2) image = brotliDecode(file + 24, e->storedLength, e->imageLength);
int32_t headerBytes = rd32(image), sectionCount = rd32(image + 4);
for (int i = 0; i < sectionCount; i++) {
    const uint8_t* d = image + headerBytes + 16 * i;
    remember(tag(d), rdu32(d + 8) /* offset */, rdu32(d + 12) /* length */);
}
// Version 3: CHGT[cell] is each cell's height in voxels; version 2: cellVoxels
// for every placed cell. tallest = the largest of them.
// Validate once: sections inside the image and sized to the header's counts,
// ROOT and COLS in range and rising, every leaf under its cell's height, runs of a
// column rising and apart, every CLRS offset at a whole record, every
// obstacle and brush index a record names in range, every jump's leaves.

// Which leaf holds point p?
int FindLeaf(Vec3 p) {
    double lx = p.x - origin.x, ly = p.y - origin.y, lz = p.z - origin.z;
    if (lx < 0 || ly < 0 || lz < 0) return -1;
    int64_t X = floor(lx / voxelSize), Y = floor(ly / voxelSize), Z = floor(lz / voxelSize);
    if (X >= columns * cellVoxels || Y >= rows * cellVoxels || Z >= tallest) return -1;
    int cell = (Y / cellVoxels) * columns + X / cellVoxels;
    int32_t root = ROOT[cell];
    if (root < 0 || Z >= height(cell)) return -1;
    int column = root + (Y % cellVoxels) * cellVoxels + X % cellVoxels;
    for (uint32_t l = COLS[column]; l < COLS[column + 1]; l++) {
        if (Z < LEAF[l].zLo) return -1;
        if (Z < LEAF[l].zLo + LEAF[l].height) return l;
    }
    return -1;
}

// Does an agent (w, h, class) fit in voxel z of leaf l, with these obstacles shut?
bool Passable(int l, int z, float w, float h, int cls, const bool* blocking) {
    const uint8_t* rec = CLRS + (cls ? LEAF[l].npcClearance : LEAF[l].playerClearance);
    uint16_t S = rd16(rec), D = rd16(rec + 2), B = rd16(rec + 4);
    double r = w * 0.5, top = origin.z + (z + 1) * voxelSize + h;
    const uint8_t* c = rec + 8;
    for (int i = 0; i < S; i++, c += 8)
        if (r > rdf(c) + EPS && top > rdf(c + 4) + EPS) return false;   // -inf compares as expected
    for (int i = 0; i < D; i++, c += 12)
        if (blocking[rdu32(c)] && r > rdf(c + 4) + EPS && top > rdf(c + 8) + EPS) return false;
    for (int i = 0; i < B; i++, c += 4)
        if (BrushOverlaps(rdu32(c), SweptBox(l, z, r, h))) return false; // separating axes, EPS
    return true;
}

// Neighbours, derived once at load: for each leaf, the touching leaf above
// and below in its column, and in each of the four side columns (the next
// cell's edge column across a cell face) every leaf whose run overlaps it.
// Store as CSR: start[leafCount + 1], entries (leaf << 3 | direction).

// A* over leaves for a walker of preset p:
//   stand in leaf l  :  Standable(l)  (bottom voxel fits, grounded & walkable or on a rim)
//   step to n        :  n a side neighbour, Standable(n), StepUp(l, n) <= stepHeight
//   jump / drop      :  JUMP records at l, class bit set, agent fits at both ends
//   cost             :  distance x LEAF[n].cost / 256
// Quick rejection: Component(p, from) != Component(p, to) means no walk or flight joins them.
```

## 14. Using the reader from the mod (C#)

The mod references `SourceSharp.MapFormats`, which has no dependencies.
After `Open`, point lookups, neighbour walks, clearance tests and leaf
reads allocate nothing (a fact checks it).

```csharp
using SourceSharp.MapFormats.Geometry;
using SourceSharp.MapFormats.Nav;

Nav3dReader nav = Nav3dReader.Open(File.ReadAllBytes("maps/level.nav3d"));  // InvalidDataException on a bad file
if (!nav.MatchesMap(worldspawn["ss_level_id"]))
    throw new InvalidOperationException("level.nav3d belongs to another build of the map");

// A map with no ss_level_id (a stock map, or one from ssmap all): bind by the map's bytes.
if (!nav.MatchesBsp(File.ReadAllBytes("maps/dm_lockdown.bsp")))
    throw new InvalidOperationException($"dm_lockdown.nav3d was built for another map ({nav.Bsp?.ToString() ?? "none named"})");

if (nav.TryGetSpawn(out Vec3 spawn, out float yaw))
    SpawnPlayer(spawn, yaw);

// Doors and other obstacles: one flag each, kept current from game events.
bool[] shut = new bool[nav.ObstacleCount];
for (int o = 0; o < nav.ObstacleCount; o++)
    shut[o] = IsClosed(nav.Obstacle(o).Name);            // e.g. "c2r0_gate"

// Any size, not only a preset: a hunched 24 x 40 NPC.
int leaf = nav.FindLeaf(npc.Origin);
foreach (Nav3dNeighbour n in nav.Neighbours(leaf))
{
    if (n.Direction >= Nav3dDirection.Up) continue;
    if (!nav.Standable(n.Leaf, nav.Leaf(n.Leaf).ZLo, 24, 40, Nav3dClipClass.Npc, shut)) continue;
    float rise = nav.StepUp(leaf, n.Leaf, Nav3dClipClass.Npc);
    if (rise > nav.StepHeight) continue;                 // a jump link, if any, covers it
    // ... A* bookkeeping, cost scaled by nav.Leaf(n.Leaf).CostMultiplier
}
foreach (int j in nav.Jumps(leaf))
{
    Nav3dJump jump = nav.Jump(j);                         // rise, columns apart, direction, class mask
}

// Presets are names for sizes; components are derived per preset at load.
int standing = nav.FindPreset("standing");
bool sameArea = nav.Component(standing, a) == nav.Component(standing, b);
```

`Nav3dReader.ToLevel()` reads everything into objects (`Nav3dLevel`) for
tools; `Nav3dWriter.Write` writes one.

## 15. A worked example

`Nav3dFileTests.Sample`: cells of 32 units, two voxels a side (voxel 16),
two columns and one row, two presets (`standing` 8 × 20 player, `crawler`
4 × 4 NPC). Cell 0 has three voxel columns with leaves; cell 1 one. Raw,
the file is 1,048 bytes (Brotli 9: 502; Deflate 6: 524). File offsets, the
image starting at `0x18`:

```
0000  53 53 4e 41 56 33 44 00 02 00 00 00 00 00 00 00   magic, version 2, codec 0
0010  00 04 00 00 00 04 00 00                            imageLength = storedLength = 1024
                              a0 00 00 00 0d 00 00 00   headerBytes 160, 13 sections
0020  02 00 00 00 00 00 00 42 00 00 80 41 02 00 00 00   2 presets, cell 32.0, voxel 16.0, cellVoxels 2
0030  02 00 00 00 01 00 00 00 00 00 00 00 00 00 00 00   columns 2, rows 1, origin (0,
0040  00 00 00 00 33 33 33 3f 00 00 90 41 00 00 60 42   0, 0), floorNormalZ 0.7, step 18, jump height 56
0050  00 00 c8 42 02 00 00 00 02 00 00 00 00 00 00 00   jump distance 100, 2 points, 2 doors, spawn 0
0060  00 00 00 00 ff ff ff ff 08 00 00 00 05 00 00 00   up arrival 0, down -1, 8 columns, 5 leaves
0070  01 00 00 00 01 00 00 00 01 00 00 00 48 00 00 00   1 obstacle, 1 brush, 1 jump, CLRS 72 bytes
0080  0f 1e 2d 3c 4b 5a 89 78 86 85 f4 e3 d2 c1 b0 a9   levelId
0090  01 23 45 67 89 ab 8d ef 81 23 45 67 89 ab cd ef   packId
00a0  00 ... 00                                          reserved to 0xb7
00b8  53 54 52 53 ff ff ff ff 70 01 00 00 4c 00 00 00   directory: STRS at image 0x170, 76 bytes
      ...                                                AGNT CELL DOOR POIS ROOT COLS LEAF CLRS DYNO BRSI BRSP JUMP
```

`ROOT` (image `0x26c`) is `0, 4`: cell 0's columns start at 0, cell 1's at
4. `COLS` (image `0x274`) is `0 1 3 3 4 5 5 5 5`: cell 0's column (0,0)
has leaf 0, (1,0) leaves 1 and 2, (0,1) none, (1,1) leaf 3; cell 1's (0,0)
leaf 4. `CLRS` (image `0x310`, file `0x328`) holds three records:

```
0328  01 00 00 00 00 00 00 00 00 00 80 ff 00 00 80 ff   offset  0: blocked, (-inf, -inf)
0338  02 00 00 00 00 00 00 00 00 00 80 ff 00 00 20 42   offset 16: 2 corners: (-inf, 40)
0348  00 00 80 40 00 00 80 ff                                      and (4, -inf)
0350  01 00 01 00 01 00 00 00 00 00 80 ff 00 00 20 42   offset 40: 1 corner (-inf, 40), 1 dynamic,
0360  00 00 00 00 00 00 80 ff 00 00 80 ff 00 00 00 00              obstacle 0 at (-inf, -inf), brush 0
```

Leaf 2's record (`LEAF` + 48, file `0x2e0`):

```
02e0  01 01 00 00 00 01 00 00 28 00 00 00 28 00 00 00   zLo 1, height 1, flags 0, cost 256, records 40 / 40
02f0  00 00 00 00 00 00 00 00                            floors 0, 0 (not grounded)
```

**Looking up** `(20, 5, 16)`: voxel `(1, 0, 1)`, cell 0, column
`ROOT[0] + 0 × 2 + 1 = 1`, leaves `COLS[1]..COLS[2]` = 1, 2; leaf 1 is
`z 0..0`, leaf 2 is `z 1..1`: **leaf 2**.

**Does `standing` (8 × 20, player) fit there?** `r = 4`, the voxel's top is
32: the corner `(−∞, 40)` has `4 > −∞` and `32 + 20 = 52 > 40.001`:
**no**. **`crawler` (4 × 4, NPC)?** `32 + 4 = 36 ≤ 40.001`, so the corner
lets it through; the dynamic corner `(−∞, −∞)` of obstacle 0 (`c0r0_gate`,
a `func_door`) blocks it **while the door is shut**; the brush (an overhang
whose underside is `z = x/2 + 32`) does not reach the swept box
`[14, 34] × [−2, 18] × [16, 36]`, whose lowest underside point is 39. So
the crawler fits with the door open, not with it shut, and
`ObstacleLeaves(0)` is `{2}`.

**Neighbours derived at load**: leaf 2's are leaf 4 (east, through the
door into cell 1), leaf 0 (west) and leaf 1 (down). Leaf 1 and leaf 4 share
jump link 0 (`rise 18.5`, player, east, 1 column): 18.5 is over the 18
step, so the walk needs the jump.

## 16. Versioning

- The **envelope's version** changes only for a change an older reader
  must not read around. **Version 2** replaced version 1's per-agent octrees,
  stored adjacency and components with the shared clearance grid; a version
  1 file is refused, not misread. **Version 3** (the rooms design, 17.11)
  adds `CHGT`, each cell's height in voxels, for a level placing a room
  taller or lower than its cell: a version 2 reader would take a tall
  cell's runs above `cellVoxels` for damage, or a low cell's space above
  its ceiling for voxels it was never told are solid, so the version
  changes. It is written only for such a level: a level of cube rooms is
  still version 2, byte for byte, and this build reads both.
- A **new optional section** gets a new tag and changes no version.
  **`BSPB`** (the binding to a compiled map, section 12) is the first: an
  older reader skips it and sees a level bound to no map, and this build
  reads a file without it the same way, so a version 2 or 3 file written
  before it is read unchanged and a level without a binding still writes
  the same bytes.
- The **header** records its size; a reader finds the directory by
  `headerBytes`, never the constant 160.
- The room pack's navigation sections carry their own **revision**: **2**
  for a cube room, **3** for a room of its own height (17.3). A pack with
  revision 1 sections reads as having no navigation:
  `ssmap link` writes the map, no `.nav3d`, and warns
  `the room pack holds no navigation for "…"; the level is linked without a
  .nav3d (compile the library with a build that writes navigation)`;
  `-require-nav` makes that an error.

## 17. Where the data comes from

### 17.1 Configuration: the library's worldspawn

| Key | Default | Meaning |
| --- | --- | --- |
| `nav` | on | `0` builds no navigation |
| `nav_voxel_size` | `16` | voxel edge; must divide the cell, at most 128 a side |
| `nav_max_slope` | (normal z ≥ 0.7) | steepest walkable floor, degrees |
| `nav_step_height` | `18` | highest step walked |
| `nav_jump_height` | `56` | highest ledge a jump link climbs, deepest drop it stands for |
| `nav_jump_distance` | `100` | farthest a jump link reaches, column centre to centre |
| `nav_cost_water` | `2` | cost multiplier of water |
| `nav_cost_ladder` | `1.5` | cost multiplier of ladders |
| `nav_agents` | `standing 32 72 player; flyer 32 32 npc` | presets `name width height [class]`; class `player`, `npc`, or a number equal to one of their masks; may be empty |

The grid does not depend on the presets. Points of interest name the
presets they apply to, and a room compile checks they fit there.

### 17.2 Authoring points of interest

An `info_poi` point entity inside a room's cell: `poi_type`, `poi_tags`,
optional `poi_radius`, `angles`, `poi_agents` (preset names; default all)
and `targetname` (`cxry_` names are room-local). A point must stand where
every preset it applies to fits, or the room fails naming the entity.
`info_poi` entities are taken out of the map. An `info_room`'s `room_role`
(`up`, `down`) gives the room's role.

### 17.3 The room pack's sections

`ssmap room` builds each room's navigation once, at turn 0, and stores it
under `NVR0`, and the same room turned one to three quarter turns under
`NVR1`-`NVR3` (a turn is an exact permutation of voxels: a fact builds a
room compiled turned and compares it bit for bit with the stored turn-0
navigation turned, at all four turns). Per turn a room's sections are
`ROOM, ECNT, LNKA, GEO`*r*`, [COL`*r*`], NAM`*r*`, NVR`*r*, so the link reads
one run of bytes per placed room.

Framing, big-endian as the pack is: `uint8` codec (0 none, 1 Deflate, 2
Brotli; **none by default**, `-nav-codec` for another), `int64` decoded length, payload. The
payload: `int32` revision (2; 3 for a room of its own height; 4 for a room
larger than one cell), `uint8` turn; `float32` cell size, voxel
size, `int32` voxels per cell edge, in revisions 3 and 4 `int32` voxels up the room's
columns (its height over the voxel, 1 to 255; in revision 3 never the edge),
in revision 4 `int32` cells along x and along y at the section's turn (a
quarter turn swaps them; the grid is that many cells of voxels, columns,
capped voxels and points across the whole footprint), `float32` floor normal z, step height, jump
height, jump distance, water cost, ladder cost, `uint8` role; presets
(`uint8` count; string name, `float32` width, height, `int32` mask);
sockets (`uint8` count; `uint8` facing, string name, `float32[3]` door
point, in revision 4 `int32` the socket's cell edge along its face); points (`int32` count; `float32` x, y, z, yaw, `uint8` has-facing,
`float32` radius, string type, string tags, `uint8` has-name and name,
`uint32` preset mask, string entity id); records (`int32` count; each three
`uint16` counts, no padding word, then the entries of 5.2, big-endian); columns (`int32` run count; per column, x fastest,
`uint16` count and the runs: `uint8` low voxel, height, key); per socket its
capped keys (`int32` count; `int32` voxel, key); obstacles (`int32` count;
string classname, `uint8` has-name and name, `int32` hammer id, `uint8`
kind, `float32[6]` bounds); overhang brushes (`int32` count; `int32` plane
count, `float32[4]` per plane). A key is `int32` player and NPC record
index, `uint16` flags and cost, `float32` player and NPC floor. A string is
`uint16` length and UTF-8.

**Caps.** The room is built with every door open, and once more per socket
with that door capped (the plug put back, the neighbour's side solid); the
section stores only the voxels whose key the cap changes. Capping adds
solids and removes none the grid counts, so a placement with several doors
capped is the open grid with each capped socket's records merged
(union of corners, re-reduced to a staircase), and the link merges exactly
that way; a fact checks it against building the room with every door shut.

### 17.4 Seams: rooms stitched against the whole map

A room is built without its neighbours. Outside its cell it assumes the
kit: solid, except behind each open door, where the neighbour's wall has
the same opening `wall_depth` deep and beyond it the neighbour's room is
open. That is exact at the seams: a voxel's record lists only the
obstacles no nearer one dominates, and beside a doorway the door's jambs
(the wall either side of the opening, floor to top) are at most half the
opening's width away and block every height, so anything in the
neighbour's room farther than the jambs is dominated. The stitched grid
therefore equals the whole level's wherever a room keeps the inside of
each door clear out to the jambs' distance plus a voxel. An open water
socket's doorway (a door whose room declares `water_<wall>`) is assumed
full of water from its floor to the declared level, as the link carves it
and the flattened level's compile fills it: the joint rule lets a water
socket meet only another at the same level, so its voxels below the level
are water. A capped one keeps its plug, solid. The flatten equivalence
fact (5.4) checks it on every 3x3 sample level, records included, and on
every level of the features and multi-library samples.

A room larger than one cell is built the same way over its footprint: one
grid, its far sides the footprint's, each face's neighbour wall cut by every
open door on it. The link writes the grid's `n x n` block of each covered
cell under that cell's root; each door stands in the cell whose edge it is
on, and a point of interest in the cell it stands in. A fact holds every
covered cell of a 2 x 2 room, at every turn, to the whole map's grid.

### 17.5 The map first, the navigation after

`ssmap link` writes the `.bsp` first, then stitches and writes the
`.nav3d`. A library host does the same through two calls:

```csharp
LevelNavPlan plan = LevelNavFromPack.Plan(layout, columns, rows, rooms, packId, levelFile,
    LevelNavFromPack.IdOptions(true, LevelNavFromPack.DefaultCompression));   // cheap; refuses capped-doorway points
if (plan.WritesNavigation) RoomCompileIds.Stamp(map, plan.PackId, plan.LevelId);
await WriteMapAsync(map);                                                     // the map never waits for navigation
if (plan.WritesNavigation)
{
    Nav3dLevel nav = await plan.BuildAsync(cancellationToken);                 // thread pool; cancellable; re-runnable
    await LevelNavPlan.WriteAsync(disk, navPath, nav, LevelNavFromPack.DefaultCompression, cancellationToken);
}
```

The build holds only memory it allocates: no file, handle, pooled buffer or
shared state, so a cancelled or failed build leaves nothing behind and the
plan can be built again. The write goes through the file system's replace,
so a cancelled or failed write leaves the previous file, never half of one.
When the navigation fails after the map is written, `ssmap link` says `the
map was written, but its navigation failed: …` and exits with failure.

### 17.6 Built from a compiled map

`ssmap nav3d <map.bsp>` builds the file for any compiled map, and
`ssmap all` writes it after vrad (`LevelNavFromBsp` in
`SourceSharp.MapTools`). Nothing in the format changes; what such a file
holds differs from a linked level's in these ways:

- **The grid** is laid over the world model's bounds: the origin is its
  low corner rounded down to the voxel, cells are 256 units (16 voxels of
  16), and every cell is as tall as the map. A map more than 16 voxels tall
  is a version 3 file whose `CHGT` gives every placed cell the same
  height; a map taller than 255 voxels is refused.
- **Cells are not rooms.** `CELL` names no room, there are no doors and no
  points of interest, and the spawn and arrivals are −1. A neighbour across
  a cell face (section 8) is only a step across a grid line.
- **Solid** is the world's brushes, each displacement's surface (every
  triangle extruded one voxel down, or along its normal for a wall or
  ceiling), and each solid static prop's collision: each convex piece of
  its model's `.phy` (or the model's hull box when the content has no
  `.phy`), turned by its angles, solid up to the piece's own upward faces
  over its outline seen from above, and down to its lowest corner. The top
  is exact, so a floor on a prop is one the game has; the sides and the
  underside are over-estimates, never under-estimates, and leave no
  downward sloped face (5.3). The pieces keep the gaps between them, which
  one box around a gate frame or a rail does not. (A box around each piece
  put a floor over nothing beside a sloped, turned or rounded piece: on
  `dm_lockdown`, 61 places a player stood with nothing within a step under
  it, found by the check below.)
- **Ladder leaves** are where a climbing player's origin is: a
  `CONTENTS_LADDER` brush and an `info_ladder` box are grown sideways by a
  player's half-width (16), as a `func_useableladder`'s climb already is, so
  a ladder close to its wall, or a solid ladder brush, has ladder leaves the
  player fits in at their centre (7.6). A voxel
  whose centre lies in a solid leaf of the compiled tree is outside the
  map's sealed hull and solid too; a cell outside throughout is empty
  (`ROOT` −1).
- **Each cell is built from the solids within 128 units of it**, so a
  record's corners are exact for every agent up to 256 units wide; a wider
  agent may be told it fits where only a wall farther off would stop it. A
  fact builds a map with 256-unit and with 512-unit cells and compares the
  standing and flying answers voxel for voxel.
- **Ids**: `levelId` and `packId` are zero and `BSPB` names the map
  (section 12).

The file is the same bytes at any thread count and on every run (cells are
built independently and stitched in cell order); a fact compares two runs,
one and four threads, and four builds at once in one process.

**Checked against the map.** `ssmap nav3d <map.bsp> -check` (`NavFloorCheck`)
reads a built file and checks that every place a player stands (7.6) has
ground under it in the map's own collision, taken exactly: the world's
player-solid brushes, each displacement's surface, and each solid static
prop's `.phy` pieces turned and placed. The player's box, at the voxel's
centre with its feet on the leaf's floor, is moved down the step height;
a place where it meets nothing is reported. An over-estimated side
(above) never fails it; an over-estimated top does, where it puts a floor
over nothing.

`dm_lockdown` (the stock map, BSP 19, 2,239 brushes, 40 displacements,
261 static props, three `func_useableladder`s), with the Source SDK Base
2013 Multiplayer content mounted, Microsoft's .NET 10 runtime, 32 threads
against one, the same bytes both ways. What a 32-wide player (72 standing,
36 crouched, `Nav3dWalker.Player`) reaches of its nine deathmatch spawns
and 77 weapons and items (a spawn's own floor must hold the player; an item
is reached from a floor within two columns and the player's height):

| build | time | bytes (Brotli 5) | leaves | spawns the player does not fit at | targets in one component |
| --- | --- | --- | --- | --- | --- |
| 16-unit voxels, prop hull boxes, fit anywhere (before) | 3.2 s / 23.5 s | 482,383 | 128,117 | 3 | 20 of 86 |
| 8-unit voxels, prop hull boxes, fit anywhere | 56.9 s / 190.9 s | 1,816,669 | 765,915 | 2 | 41 of 86 |
| 8-unit voxels, prop pieces, ladders grown, fit at centre | 41.1 s (32 threads) | 2,334,724 | 855,012 | 0 | 68 of 86 |
| 16-unit voxels, prop hull boxes, fit at centre | 3.2 s | 482,383 | 128,117 | 0 | 50 of 86 |
| 16-unit voxels, prop pieces' boxes, ladders grown, fit at centre | 3.6 s / 21.9 s | 598,892 | 143,399 | 0 | 86 of 86 |
| **16-unit voxels, prop pieces to their own tops, brush entities at their origins, sheer steps walked** | **4.5 s / 30.1 s** | **733,923** | **156,159** | **0** | **86 of 86** |

The last row was measured on a 32-core machine under a load average near
47, so its times are high against the earlier rows'; its file is SHA-256
`84740bf8…` at 1 and at 32 threads. `ssmap nav3d -check`
finds, of its 35,054 places a player stands, none without ground within a
step under the player's box; the row before it had 61 such places (of
35,152), every one on the box around a sloped, turned or rounded prop
piece.

Three things close the gap, and a finer voxel is none of them: the fit at
the voxel's centre (7.6, a reader query on the same records), the static
props' collision pieces in place of one box each (a hull box closes the
openings between a prop's pieces), and the grown ladder volumes. Without
the game's content (no props at all) the same map gives 86 of 86 at the
centre and 44 of 86 anywhere; a fact checks both on the repo's copy.

## 18. Measurements and the choices they made

A 4-core machine shared with other work, Microsoft's .NET 10 runtime; wall
times are noisy and given as ranges or medians of interleaved runs.
Version 1 is `main` before this change.

**Byte identity.** `ssmap link -no-nav` on the eight sample levels gives the
same map as version 1 (SHA-256 `174b2214…` rooms3x3, `3d6975b2…` turn1,
`1f7982a0…` turn2, `d403c169…` turn3, `b1010f30…` seed_1, `7be65827…`
seed_10, `2e1cca13…` seed_2, `db77def5…` seed_9), and the 256-room stress
level's `-no-nav` map is `2776687167…` with both.

**The 3x3 sample** (5-room library, 9-room levels):

| | version 1 | version 2 |
| --- | --- | --- |
| `ssmap room` | 0.87 s | 1.00-1.09 s |
| `.roompack` | 772 KB | 517 KB (raw sections, the default); 223 KB with Brotli 5 |
| `ssmap link` (8 levels) | 0.44-0.66 s | 0.44-0.64 s |
| `.nav3d` | 717-941 KB (raw) | 3.5-4.5 KB (Brotli 5); 59.6 KB raw for rooms3x3 |

**The stress library** (256 rooms, a 16x16 level from `ssmap layout -seed 1`):

| | version 1 | version 2 |
| --- | --- | --- |
| `ssmap room` (wall) | 7.9 s | 10.2-11.4 s (5.0-5.4 s with `nav 0`) |
| `.roompack` | 40.3 MB | 27.1 MB (raw sections, the default); 11.9 MB with Brotli 5 |
| `ssmap link` (wall) | 1.43-1.59 s | 1.11-1.25 s (`-no-nav`: 0.71 s) |
| `.nav3d` | 28.3 MB | 117 KB (Brotli 5), 1.85 MB raw: 62,602 leaves, 861 points, 1,300 jump links |

The room build costs more than version 1's (about 21 ms a room in a cold
process against 11 ms): each socket's capped state is a full rebuild,
chosen because it is the plain statement of what capping means and is
what the facts compare with. The link, which runs far more often, is
faster, and the file is 240 times smaller.

**`.nav3d` codecs** (16x16 stress level; decode is the codec alone, open is
`Nav3dReader.Open` including validation and everything derived at load,
medians in a warm process):

| codec | bytes | saving | encode | decode | open |
| --- | --- | --- | --- | --- | --- |
| none | 1,845,448 | 0% | 9 ms | 0.3 ms | 20 ms |
| deflate:1 | 547,081 | 70.4% | 13 ms | 2.5 ms | 19 ms |
| deflate:6 | 185,492 | 89.9% | 18 ms | 1.3 ms | 21 ms |
| deflate:9 | 134,427 | 92.7% | 81 ms | 1.3 ms | 20 ms |
| brotli:1 | 247,871 | 86.6% | 10 ms | 2.7 ms | 24 ms |
| **brotli:5** | **117,330** | **93.6%** | 29 ms | 2.2 ms | 17 ms |
| brotli:9 | 97,684 | 94.7% | 54 ms | 1.6 ms | 17 ms |
| brotli:11 | 79,895 | 95.7% | 1,710 ms | 1.8 ms | 21 ms |

The owner's rule was to compress by default if it saves more than 30%;
every codec saves over 70%, and decoding is a few milliseconds against the
load's derivation. Brotli 5 is the default: 94% smaller, 29 ms to encode,
where Brotli 11 saves 2 points more for 60 times the encode.

**Room pack `NVR` sections** (the same library's 1,024 sections, four turns;
read is `RoomNavSection.Read` of all of them):

| codec | bytes | saving | encode | read |
| --- | --- | --- | --- | --- |
| none | 16,218,544 | 0% | 61 ms | 21 ms |
| deflate:1 | 2,385,620 | 85.3% | 64 ms | 31 ms |
| deflate:6 | 1,435,943 | 91.1% | 128 ms | 31 ms |
| deflate:9 | 1,422,423 | 91.2% | 433 ms | 34 ms |
| brotli:1 | 1,535,194 | 90.5% | 84 ms | 42 ms |
| **brotli:5** | **981,159** | **94.0%** | 188 ms | 35 ms |
| brotli:9 | 969,080 | 94.0% | 2,129 ms | 35 ms |
| brotli:11 | 914,405 | 94.4% | 26,220 ms | 39 ms |

The pack's sections stay **raw by default**: the owner's rule for the pack
is that link speed beats disk size, and raw is the faster read (21 ms
against 35 ms for every section). Whole cold links of the 16x16 level tied
inside their noise (medians 1.43 s raw, 1.47 s from a Brotli 5 pack, 1.50 s
from a turn-0-only pack), which does not override the rule;
`ssmap room -nav-codec brotli` gives the fifteenfold smaller pack (27.1 MB
to 11.9 MB) to a host that wants it. The four turns stay stored. All three give the same `.nav3d` apart from
the ids (the pack id covers the pack options).

`-nav-codec none|deflate[:0-9]|brotli[:0-11]` on `ssmap room` and
`ssmap link` chooses otherwise. Compressed bytes are the same on every run
and at any thread count (a fact runs the room compile and link at 1 and 4
threads, twice each, and compares the pack, the `.nav3d` and the map);
Brotli and raw bytes are pinned by the facts, and the Deflate pin is
Microsoft's runtime's zlib-ng.
