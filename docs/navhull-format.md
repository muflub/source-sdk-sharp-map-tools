# The `.navhull` player-hull navigation file

`ssmap nav3d <map.bsp> -hull` writes `<map>.navhull` beside a compiled map:
the free space of the map's play area at 16-unit voxels, as boxes, with
what a body of a few given widths needs to know there (whether it fits at
a height, whether it stands, the floor under it). This document is the
file's specification, version **1**, written so a reader can be built from
it alone. The C# reader in `SourceSharp.MapFormats` (`NavHullReader`) is
one such reader, and the facts check it against the `.nav3d` of the same
map (`docs/nav3d-format.md`).

## 1. Why a second file

A `.nav3d` leaf stores exact clearance for **every** agent size: a
staircase of `(width, top)` corners naming every solid within reach. Near
any wall that staircase changes from one voxel column to the next, so
leaves barely merge across columns. That is fine for an ordinary map
(koth_viaduct: 5.2 million leaves, 20 MB), but a jump map's rooms stand
over the whole height of a 32,768-unit world, and jump_academy2's
play space at 16 units is 126 million leaves: about 4.5 GB, past the
format's 2 GB image and far more than a game server should hold.

A game whose agents are one body needs much less. TF2's players are all
48 units wide, 82 tall standing and 62 crouched, and its bots test a
drop with a 32-wide body. For a body of width `w` centred on a column,
the whole clearance answer is **one number**: the lowest solid it meets
above the column (section 4). Keyed on that, runs of voxels are long and
neighbouring columns agree, so they merge into boxes. The same map is
3.9 million boxes, 27 MB stored, built in about a minute.

| Map | `.nav3d` | `.navhull` (widths 32, 48) |
| --- | --- | --- |
| koth_viaduct | 5.2 M leaves, 20 MB, 5.5 s | 263 k boxes, 1.8 MB, 4.3 s |
| jump_academy2_rc9a | 126 M leaves, ~4.5 GB image (not writable), 448 s, 24 GB | 3.9 M boxes, 27 MB, 68 s, 2.8 GB |

## 2. Coordinates and conventions

As the `.nav3d` file (its section 2): Source units, z up, little-endian,
IEEE binary32, half-open voxels (a point on a boundary belongs to the voxel
above it), `ε = 0.001`. The grid is `columns × rows` cells of `cellVoxels`
voxel columns a side, cell `(column, row)` at index `row × columns +
column`, each `columnVoxels` voxels tall from `origin.z`. It is the grid the
`.nav3d` of the same map at the same voxel lays, line for line.

## 3. Layout

```
envelope (24 bytes, never compressed)
stored image (raw, or compressed by the envelope's codec)
    header (headerBytes, 96 in version 1)
    section directory (sectionCount × 16 bytes)
    sections (each on a 4-byte boundary, zero padding between)
```

**Envelope**: the `.nav3d` envelope's layout with its own magic.

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `char[8]` | magic `SSNAVHUL` |
| 8 | `int32` | version: **1** |
| 12 | `uint8` | codec: 0 none, 1 Deflate (raw), 2 Brotli |
| 13 | `uint8[3]` | zero |
| 16 | `int32` | `imageLength` |
| 20 | `int32` | `storedLength`: the file is exactly `24 + storedLength` bytes |

**Header** (image offset 0):

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `int32` | `headerBytes`: 96. The directory starts here. |
| 4 | `int32` | `sectionCount` |
| 8 | `float32` | `voxelSize` |
| 12 | `int32` | `cellVoxels`, 1 to 128 |
| 16 | `int32` | `columns` |
| 20 | `int32` | `rows` |
| 24 | `float32[3]` | `origin` |
| 36 | `int32` | `columnVoxels`, 1 to 65,535 |
| 40 | `int32` | `widthCount`, 1 to 16 |
| 44 | `float32` | `floorNormalZ` |
| 48 | `float32` | `stepHeight` |
| 52 | `float32` | `jumpHeight` |
| 56 | `float32` | `jumpDistance` |
| 60 | `int32` | `boxCount` |
| 64 | `int32` | `keyCount` |
| 68 | `int32` | `shutCount` |
| 72 | `int32` | `obstacleCount` |
| 76 | `int32` | `jumpCount` |
| 80 | `byte[16]` | zero, reserved |

**Directory**: `sectionCount` entries of 16 bytes: `char[4]` tag, `uint32`
zero, `uint32` offset from the image's start (a multiple of 4), `uint32`
length. A reader ignores tags it does not know.

## 4. Sections

| Tag | Record | Count |
| --- | --- | --- |
| `STRS` | bytes | string table: NUL-terminated UTF-8, offset 0 the empty string |
| `WDTH` | `float32` | `widthCount`: the built widths, rising, each above 0 |
| `CELB` | `uint32` | `cells + 1`: cell `c`'s boxes are `[CELB[c], CELB[c+1])` |
| `BOXS` | 12 bytes | `boxCount` |
| `KEYS` | `16 + 4 × widthCount` bytes | `keyCount` |
| `SHUT` | `4 + 4 × widthCount` bytes | `shutCount` |
| `DYNO` | 48 bytes | `obstacleCount`: the `.nav3d` `DYNO` record |
| `JUMP` | 20 bytes | `jumpCount` |
| `BSPB` | 40 bytes | one, **optional**: the compiled map's binding, as in the `.nav3d` (its section 12) |

**`BOXS`** (per box; a cell's boxes are together, in cell order)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint8` | `x0`: first voxel column along x, in the cell |
| 1 | `uint8` | `y0` |
| 2 | `uint8` | `x1`: one past the last column along x, at most `cellVoxels` |
| 3 | `uint8` | `y1` |
| 4 | `uint16` | `zLo`: the lowest voxel, from the grid's floor |
| 6 | `uint16` | `height`: voxels up, at least 1 |
| 8 | `uint32` | key |

A box is free space: its voxels are exactly voxels a `.nav3d` leaf of the
same map would hold (free for at least one clip class), and every such
voxel is in exactly one box. Boxes in one column never overlap.

**`KEYS`** (per key; boxes share keys)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint16` | flags: 1 water, 2 ladder, 4 grounded, 8 walkable, 16 player-blocked |
| 2 | `uint16` | cost, 8.8 fixed point |
| 4 | `float32` | floor: the altitude of the solid under the box's bottom voxel when grounded, else 0 |
| 8 | `uint32` | first shut record |
| 12 | `uint32` | shut record count |
| 16 | `float32[widthCount]` | ceiling per width, every obstacle open |

Grounded and walkable are the player class's, and describe the box's
**bottom** voxel only; the voxels above it have no floor of their own (the
build never lets a box continue up through a floor). Player-blocked marks
space a player clip closes to players but not to a point (a sight line, a
rocket): every ceiling of such a key is `−∞`.

**The ceiling.** For width `w`, the ceiling is the lowest altitude a body
`w` wide, square, centred on a column of the box, meets solid at. A body
of height `h` with its feet at `z` fits there exactly when

```
z + h ≤ ceiling + ε
```

`−∞` means the width fits at no height (a wall is within its footprint at
the box's own height); `+∞` means nothing is above. It is the answer the
`.nav3d` reader's `FitsAtCentre` gives for the same voxel: a clearance
record blocks a centred body exactly when `z + h > T + ε` for some corner
`(R, T)` with `w/2 − voxelSize/2 > R + ε`, so the least such `T` is the
ceiling, for every height at once. Where a voxel lies under an
**overhanging brush** (one whose underside slopes), the ceiling is the
voxel's bottom plus the head room the brush leaves a centred body standing
there.

**`SHUT`** (per (key, obstacle)): `uint32` obstacle, then `float32` per
width: the key's ceilings with that one obstacle shut (a door closed, a
prop at its place) and every other open. A key lists an obstacle only when
shutting it lowers a ceiling.

**`JUMP`** (per link)

| Offset | Type | Field |
| --- | --- | --- |
| 0 | `uint32` | box A (the lower-numbered) |
| 4 | `uint32` | box B |
| 8 | `uint8` | A's column along x, in A's cell |
| 9 | `uint8` | A's column along y |
| 10 | `uint8` | B's column along x, in B's cell |
| 11 | `uint8` | B's column along y |
| 12 | `float32` | rise: B's floor minus A's |
| 16 | `uint8` | direction from A to B (0 east, 1 north, 2 west, 3 south) |
| 17 | `uint8` | columns apart: 1 a ledge, more across a gap |
| 18 | `uint16` | zero |

Links are the player's, by the `.nav3d` rule applied to boxes' columns
(section 6), sorted by box A, box B, then the columns.

## 5. Queries

- **Column**: a cell's column `(x, y)`'s boxes are the cell's boxes whose
  footprint holds it, sorted by `zLo`. A reader derives this index at load.
- **Point** (`FindBox`): the point's voxel, then its column's boxes; the box
  whose `[zLo, zLo + height)` holds the voxel's z, or none (solid, an empty
  cell, or outside the grid).
- **Below / above** a box in one of its columns: the previous or next box
  of that column, when it touches (`below.zLo + below.height == zLo`).
- **Neighbours**: the boxes overlapping a box across each side face (into
  the next cell too), then those touching it from above and below.
- **Fits** at feet `z` for width `w` and height `h`: `z + h ≤ ceiling(w) + ε`.
  A width the file does not list has no answer.
- **Stands**: walkable, and fits with the feet on the floor.
- **Climbs**: a ladder box the body fits in with its feet on the box's
  bottom voxel's bottom.
- **Stands with an obstacle shut**: as stands, with the shut record's
  ceiling for that obstacle when the key lists one.

## 6. How it is built

`HullNavFromBsp` lays the grid `LevelNavFromBsp` lays, builds each cell's
clearance grid exactly as for a `.nav3d`, and then reduces each run of a
column to its key: the run's player flags, cost, floor, a ceiling per
width from its clearance record, and its shut records. Touching runs whose
keys agree but for the floor join (the upper one never has a floor).
Cell by cell, each run not yet taken grows east over columns holding the
same run (same `zLo`, height and key), and the strip north over rows whose
same columns all hold it: a box.

Jump links follow the `.nav3d` link's rule over boxes' columns: from each
column of a walkable box, straight along the four directions up to
`jumpDistance`, a walkable box whose floor rises more than `stepHeight`
(one column away) and at most `jumpHeight` either way, when the take-off
and landing columns are open to the player from their bottoms up to the
higher floor's bottom voxel, and every column between is open at that
voxel with no floor within a step of either end. A cell with no boxes ends
the search that way.

The file is the same bytes at any thread count: cells are reduced into
their own slots, keys are numbered in cell order as first met, and links
are sorted.

## 7. Versioning

The version changes only for a change an older reader must not read
around. A new optional section gets a new tag; `headerBytes` lets a later
header grow.
