# Rooms door slots sample

A room library whose door kit has **three door slots** per cell edge
(`door_slots 3` on every `info_room`), and a level that uses them. Written
by `RoomsSample --slots samples/rooms-slots` (`RoomsSlotsSample`); only this
README is by hand.

## The kit

Cell 256, walls 8 deep, doors 64 wide and 240 high. `door_slots 3` divides
each cell edge into three equal parts and centres a door in each, rounded to
whole units and mirrored about the middle: the doors stand at **11, 96 and
181** along the edge, counted from the room's own south (east and west
walls) or west (north and south walls). The middle slot is where a kit of
one slot puts its only door. A kit is refused when its first door leaves
less than the wall depth to the corner, with the numbers
(`N x (door_width + 2 x wall_depth)` is the edge it needs).

Each door here is wide enough for the navigation grid's 16-unit voxels to
pass the 32-unit player: a door must be at least the agent and a voxel wide
where the grid meets it, so the 48-unit doors a 16-deep wall leaves room for
would not be walkable on the nav grid.

## The rooms

| Room | Doors (socket names) |
|---|---|
| `twin` | `east_a`, `east_c` (two doors on one wall), `west_b` |
| `corridor` | `east_a`, `west_a`: it enters its neighbours at a corner slot |
| `corner` | `east_a`, `west_a`, `north_a`, `south_a` |
| `three` | `east_a`, `east_b`, `east_c`, `north_a`, `south_a` |
| `ring` (2 x 2) | every slot of every cell edge: `east0_a` .. `south1_c` |

A socket's name is its wall, its cell edge in a larger room, an underscore
and its slot's letter; `socket_<name>` renames one.

## The level

```
+,     +,       ~
ring,  +,       corner@180
three, twin@180, ~
corner, corridor, corner
```

Two rooms join only where their doors stand at the same place on the shared
wall; every other slot is capped. The twin turned a half faces its east doors
west, slot for slot reversed, so the three-slot room's `east_a` meets its
`east_c` and `east_c` its `east_a`, and `east_b` is capped. The corridor
joins the corner rooms along the south slots; the ring's `east0_c` meets the
turned corner room's `east_a`, which the half turn put at the north of its
west wall.

## Commands

```
ssmap room samples/rooms-slots/rooms.vmf -game samples/rooms-slots
ssmap rooms samples/rooms-slots/rooms.vmf
ssmap link samples/rooms-slots/levels/slots.yaml -out samples/rooms-slots/out/slots.bsp
ssmap link samples/rooms-slots/levels/slots.yaml --flatten -out samples/rooms-slots/maps/slots.vmf
ssmap nav samples/rooms-slots/out/slots.nav3d
```

`ssmap rooms` prints each socket with its slot (`slot 1 of 3`). `ssmap layout`
lays out libraries of one slot only, and says so for this one: write a level
of slot rooms by hand.
