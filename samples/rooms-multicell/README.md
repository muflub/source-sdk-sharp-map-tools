# Rooms multi-cell sample

Rooms larger than one cell (the rooms design, 17.8), on the 3x3 sample's
kit (256-unit cells, 96 x 224 doors, 16-unit walls), placed among rooms of
one cell. Everything here but this README is written by
`tools/RoomsSample --multicell samples/rooms-multicell`, and a test holds
the checked-in files to the generator (`RoomsMultiCellSample`).

| Room | Footprint | What it shows |
| --- | --- | --- |
| `hub` | 1 x 1 | four doors: the rooms of one cell the level joins the others with |
| `hall` | 2 x 2 | doors at `east0`, `west1`, `north0`, `south1`; a pillar across both internal cell faces |
| `court` | 3 x 3 | a door at the middle cell edge of each side; a low wall across an internal cell face |
| `gallery` | 1 x 2 | doors at both ends and at `east1` |
| `atrium` | 2 x 2, 512 tall | a displacement across the internal face x = 256 and a pool across y = 256 |

A larger room's `info_room` says `room_footprint "W D"` (cells along the
room's own +x and +y); its sockets are found per cell edge of its perimeter
and named `east0`, `east1`, ... from its own south (east and west sides) or
west (north and south sides). Navigation is built as for any library: one
clearance grid over each larger room's footprint, written into the level's
`.nav3d` cell by cell.

`levels/multicell.yaml` places them, every room reached through a door; a
larger room is written in its south-west cell and `+` marks each other
cell it covers. `multicell_turn1` to `_turn3` are the level turned whole a
quarter at a time, so every room is linked at every turn.

```sh
ssmap room samples/rooms-multicell/rooms.vmf -game samples/rooms-multicell
ssmap rooms samples/rooms-multicell/rooms.vmf
ssmap link samples/rooms-multicell/levels/multicell.yaml -out samples/rooms-multicell/out/multicell.bsp
ssmap link samples/rooms-multicell/levels/multicell.yaml --flatten -out samples/rooms-multicell/maps/multicell.vmf
```
