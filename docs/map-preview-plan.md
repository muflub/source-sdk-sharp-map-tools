# Map preview and playability tool for Claude: implementation plan

First drafted by a Fable planning agent that read the tree, then revised. The goal: give Claude feedback on the maps it builds, both how they look (rendered views with real textures, models and baked lighting) and whether they play (a real player, including TF2 players flying through the air from rocket, sticky and grenade jumps or pyro airblast, can reach every intended place and never gets stuck or snagged).

Contents: 0 what exists · 1 decisions · 2 architecture · 3 input and speed · 4 materials · 5 models · 6 rendering and annotation · 7 cameras · 8 checks · 9 player movement and reachability · 10 interface · 11 testing and calibration · 12 reference library in `ssassets` · 13 phases · critical files.

## 0. What is actually there

- **AssetDb (`ssassets`, `src/SourceSharp.AssetDb/`, merged in #137)** is an exe with one `import` verb writing an SQLite `assets.db`, a content-addressed images folder and an agent README (`Database/AgentReadme.cs`), plus `list`, `remove` and a read-only `query "<sql>"`. Tables: `games`, `textures`, `game_textures`, `materials` (`shader`, `usage`, `category`, `keywords`, `surface_prop`, ...), `models` (size, mesh, hull and view bounds, `static_prop`, `has_collision`, ...). Reusable code: `Models/ModelRenderer.cs` (`CollectTriangles`, the VTX/VVD/MDL lockstep walk; `FillTriangle`'s inclusive edge rule and strict-greater z tie-break), `Imaging/PngEncoder.cs` (deterministic), `RgbaImage.cs`, `BitmapFont.cs`, `ImageStats.cs`, `GameMount.cs`, `ContentWalk.cs`, `AssetIndex.cs`, `ImportIssue.cs`.
- **`SourceSharp.MapTools.Gpu`** is compute/ray-query only (BLAS/TLAS + a ray-query kernel; no graphics pipeline), exposed as `IRayTracer` and loaded by name through `HostBackends`.
- **2D overview**: `MapFormats/Map2d/Map2dSvg.cs` draws a `Map2dLevel`; `MapTools/Rooms/LevelMapBuilder.cs` `FromCompile(BspData, checksum)` builds it from any BSP (`RoomMapFaces.Walkable` + `MapPolygonUnion`); `ssmap map2d` exists.
- **nav3d from BSP (38446d1)**: `MapTools/Nav/LevelNavFromBsp.cs` builds a voxel `Nav3dLevel` from world brushes, displacements and solid static props; `Nav3dWalk.Components(reader, walker)`; `NavFloorCheck.Check`; `NavInspector.Obj`. `Nav3dReader` carries `StepHeight` and `JumpHeight` (defaults 18 and 56 in `NavSettings`, overridable by worldspawn keys) and jump links. `Nav3dWalker(Width, StandHeight, CrouchHeight, ClipClass, Fit)` is a record; `Nav3dWalker.Player` is HL2's `(32, 72, 36)`.
- **Player hull**: `MapTools/Rooms/PlayerHull.cs` is HL2's 32×72. **TF2's hull is 48×48×82 standing and 62 ducked**, so every player-shaped test needs a per-game profile, not these constants.
- **Collision**: BSP structs for `DBrushSide`, `DPhysModel`, `DispTri` and the `PhysCollide` lump exist; `Phys/` has a managed cooker (`ManagedCollisionSession`, `ManagedTrace`: rays and zero-length overlap tests on cooked convexes, GJK) and the optional native vphysics path (`-cooker native`); `MapFormats/Assets/PhyFile.cs` reads model collision. **There is no swept player box against brushes, displacements and prop convexes**; that is new work (section 9.2).
- **Lighting readers**: `Rad/Ambient/AmbientScene.cs`, `ColorRgbExp32.ToLinear()`, `Disp/DispLightingLoader.Load`, `Rad/Light/BumpBasis.cs`, `Rad/Light/DirectLightBuilder.Build`, `Bsp/InstanceTransform.FromAngles`, `Rad/StaticPropModelLoader`, `Rad/Props/StaticPropLighting` (vhv), `Rad/VradSkybox.cs`.
- **Design intent**: room levels are YAML (`MapTools/Rooms/LevelYaml.cs`, `LevelFileException`), linked by `ssmap link` (`LevelLinker.*`); `SourceSharp.RoomContracts` carries the entity contract (`LogicRoom`, `LevelMap.MarkerKey`/`LabelKey`).
- **Compile**: `MapCompiler.CompileAsync(CompileRequest)` with `InMemoryCacheStore`, `PropHullCache`, shared `Content`, `Parallel.Pool`; `VradOptions.FastDefault`.
- **Existing checks**: leaks (`VbspResult.Leak` / `LeakReport.Path`, `.lin` via `LeakTrace`), `ssmap check` (`Validation/`, `BspValidator.CheckAsync`), nav3d components and floor checks.
- **Fixtures**: `[SandboxMapFact]`, `SyntheticContent.Build()`, `Tests/AssetDb/StudioTestModel.cs`, `Tests/Fixtures/models/hs_sign/`; `SourceSharp.MapGen` writes synthetic maps.
- **Comparison game**: a local TF2 install, mounted through `source-sdk-2013/game/mod_tf` (its `gameinfo.txt` pulls in `|appid_440|`, i.e. Steam's `Team Fortress 2/tf`). The same SDK tree has the TF2 game code (for movement constants) and a buildable `mod_tf` server for recording calibration outcomes. Facts that need it skip when it is absent.
- **Rule tests**: `LibraryRuleTests.LibraryAssemblies`, `DeterministicMathRuleTests`, `FileSystemSeamTests` must be extended to any new library.

## 1. Decisions that keep it simple

1. **BSP input only.** Claude already compiles with `ssmap`; the preview renders the compiled BSP with the lightmaps vrad baked. No second, approximate lighting model that can disagree with vrad and mislead Claude. A direct VMF path stays unscheduled behind a measurement gate (section 3.3).
2. **Gather existing checks; write only what is new.** Leaks come from the compile, BSP validation from `ssmap check`, ground reachability from nav3d. New code: prop placement, entity I/O, design conformance and the movement simulator.
3. **Minimal materials first**: base texture × lightmap, alpha test, translucency, tools hidden, sky, missing-asset checker. Bump maps, envmaps, `$detail`, blends and water come only once Claude's use shows they matter.
4. **CLI and files first.** `ssmap preview` and `ssmap movement` write images plus JSON that Claude Code reads directly. The MCP server is unscheduled until a need appears.
5. **Tags are SQL views, not code** (section 12.3).
6. **Determinism everywhere**: IEEE arithmetic plus `DetMathF`, fixed seeds; the same input gives the same images, the same trajectories and the same report on every OS.

## 2. Architecture

```
SourceSharp.MapFormats           <- (move) Imaging/{PngEncoder,RgbaImage,BitmapFont,ImageStats}, Assets/StudioMeshReader
SourceSharp.MapTools             + Movement/ (hull traces, movement sim, reachability, airborne fuzzing)
                                 + MapFacts/ (map statistics shared by ssmap and ssassets)
SourceSharp.MapPreview           NEW library: scene, renderer, cameras, overlays, report. Refs MapFormats, MapTools.
SourceSharp.MapCompile (ssmap)   + PreviewCommand.cs ("preview"), MovementCommand.cs ("movement")
SourceSharp.AssetDb (ssassets)   + Maps/ (reference library: facts first, shots via MapPreview later)
unscheduled: SourceSharp.PreviewServer (MCP), SourceSharp.MapPreview.Gpu (Vulkan raster)
```

`MapPreview` and the new `MapTools` folders follow MapTools' rules (no mutable statics, no packages, IO via `IFileSystem`/`IContentFileSystem`, `DetMathF`, tokens last, no Console); add `MapPreview` to the three rule tests. Movement lives in MapTools rather than MapPreview because `ssmap check`, the bots work in the SourceSharp mod and the reference library all want it without the renderer.

**Renderer: CPU software rasteriser.** Perspective-correct, z-buffered, tile-parallel on `CompilePool` workers, generalised from `ModelRenderer.FillTriangle` (same edge and tie rules). Source maps are small and output images are small, so a view renders in well under a second, and the result is bit-identical on Linux, Windows and macOS. Culling: frustum plus PVS (`VisibilityLump.DecompressRow` for the camera's cluster via `BspTreeView.LeafOf`). Vulkan raster stays unscheduled behind an `IPreviewRenderer` seam, built only if measurements show the CPU path is too slow.

**Core types (`src/SourceSharp.MapPreview/`)**: `Scene/PreviewScene.cs` (immutable: meshes, materials, per-face lightmaps, ambient cubes, entities, bounds, spawns, sky), `Scene/BspSceneBuilder.cs`, `Materials/PreviewMaterialLibrary.cs`, `Render/{SoftwareRasterizer,Framebuffer,Shading,Tonemap}.cs`, `Cameras/{CameraPlanner,PreviewCamera}.cs`, `Overlays/*`, `Checks/*`, `Report/{PreviewReport,PreviewJsonContext}.cs` (System.Text.Json source generation), `PreviewSession.cs` (long-lived, per game and map: borrowed mount, byte-bounded texture and model caches, last scene and cameras; disposable; nothing static).

## 3. Input and "near real time"

### 3.1 BSP scene

`BspFile.LoadAsync`; `BspStructView.As<T>` over faces, edges, surfedges, texinfo, texdata, planes, models, leaves, nodes; `EntityLump.Parse`; `StaticPropLump.Read`; `DispLightingLoader.Load`; `VisibilityLump.Read`; leaf ambient lighting (LDR and HDR); the pak (`PakArchive`) mounted in front of the game so patched VMTs and embedded assets resolve as the engine does.

- Lightmaps: `LightOfs`, luxel mins and size, `Styles`; luxel UV from `TexInfo.LightmapVecsLuxelsPerWorldUnits`, the arithmetic in `Rad/Light/FaceSampleBuilder.cs` and `Disp/LightmapSamplePositions.cs` (call or mirror, and test against them). Bumped faces use the flat lightmap until bump rendering exists. Style 0; others at their default average and listed.
- Static props: `InstanceTransform.FromAngles`; vertex colours from `sp_N.vhv` when present, else the nearest leaf ambient cube.
- Brush entities: `DModel[i]` at `origin`; triggers, clips and illusionary hidden unless the tools overlay is on.
- Sky: 2D skybox cube from `worldspawn.skyname`; 3D skybox from `sky_camera` later.

### 3.2 Speed

- **Compile settings for previews**: `ssmap all` with fast vvis and `VradOptions.FastDefault` (no bounces, no supersampling). Its time on the sandbox map and one TF2-sized map is measured in phase 1 and recorded in the README.
- **`-watch` is the normal loop.** A CLI call starts cold (mount, decode every texture); a watching process keeps its `PreviewSession`, so after an edit it pays only for the recompile, changed assets and rasterising. The host polls `IFileSystem.GetInfoAsync` snapshots; `RecordingContentFileSystem` gives each scene's dependency set for invalidation.
- **Textures from the asset database**: with `-assets <db dir>`, the preview reads the already-decoded 512 px PNGs from `ssassets` instead of decoding VTFs, falling back to VTF decode for anything missing. One-off cold runs get fast too.
- **Deferred shading**: rasterise IDs, depth and UVs first; texture and light only the pixels that survive the depth test.
- **Render at output size**: contact sheet tiles are drawn at 512 px directly; individual 1024×768 views only when asked for.
- **Texture mips** chosen per view so the largest decoded level matches screen size.

### 3.3 VMF path (unscheduled, gated)

Build it only if the measured fast compile is too slow for the edit loop (more than ~10 s on a typical map). It would run vbsp in process and light with shadow rays through `IRayTracer`; until then the compile is the preview's first step.

## 4. Materials

`PreviewMaterialLibrary.ResolveAsync(name)`: `MaterialFactsReader.Normalize`, `VmtDocument.ReadAsync`, `VmtPatchResolver.ResolveAsync`, `MaterialShaderTable.RuleFor`, opacity per `MaterialFacts.Opacity`.

- **Phase 1 set**: `$basetexture` (or the asset db PNG), `$alphatest`/`$alphatestreference`, `$translucent` (sorted after opaques), `$color`, `$selfillum`, `UnlitGeneric`, sky, `tools/*` and compile flags hidden by default and colour-coded in the tools overlay, `$nocull`.
- **Missing VMT/VTF or undecodable format**: magenta/black checker plus a report entry listing every face and prop that uses it.
- Shading: `textureLinear × lightmapLinear` (×2 LDR overbright), bilinear luxel sampling, const sRGB tables; LDR clamp, or HDR log-average exposure plus Reinhard via `DetMathF`. Exposure and histograms go in the report, so Claude can tell "unlit" from "exposed for the sun".
- **Deferred until use shows the need**: bumped lightmaps (`BumpBasis`), WorldVertexTransition blends, `$detail`, envmap tint, water, 3D skybox.

## 5. Models

Move `ModelRenderer.CollectTriangles` to `src/SourceSharp.MapFormats/Assets/StudioMeshReader.cs` (LOD0 positions, normals, UVs, material index; VTX flavour as a parameter: dx90 then dx80 for the preview, dx80 kept for AssetDb so its images do not change). Skins via `MdlFile.SkinTable()`, material lookup as `ModelExporter.ResolveMaterial` does. Static props from the game lump; `prop_dynamic`, `prop_physics`, door classes from the entity lump (`rendermode 10` hidden). Missing model: magenta hull box plus report entry. Per-session byte-bounded `ModelMeshCache`.

## 6. Rendering and annotation for a vision model

- **Scale reference in every perspective shot**: a player silhouette at the profile's hull size (TF2: 48×82) standing on the floor nearest the view's focus, plus a scale bar on orthographic views. Proportions are where a vision model misjudges most; a person-sized reference fixes most of it.
- **Numbered markers**: every flagged item (floating prop, snag cluster, dangling output, unreachable area, missing asset) gets a number drawn in the image as a bright `[n]` tag with a leader line, and the same number in the JSON (`checks.props[3]` is `[3]` in the picture). Claude can point at a problem and look up its coordinates instead of guessing them.
- **Labels**: the bitmap font (moved from AssetDb) for camera ids and descriptions on sheet tiles; entity labels capped and prioritised (spawns, objectives, doors, named entities).
- **Overlays** (`-overlay`): `entities`, `leak`, `reach` (reachability tiers, section 9), `snags`, `io`, `lightmap` (luxel density false colour), `missing`, `tools`, `none`.

## 7. Cameras

`CameraPlanner.Plan(scene, options)` → `PreviewCamera[]` (id, kind, position, forward/up, fov or ortho extent, size, layers, description, why):

1. **Overview**: top-down ortho with a ceiling cutaway, z-band shading from `LevelMapBuilder.FromCompile`, scale bar; plus an isometric 3/4 view.
2. **Spawns**: one per team (`info_player_teamspawn` by `TeamNum`, plus HL2/CS classes), eye at origin + profile eye height, yaw from `angles`.
3. **Rooms**: nav components clustered with fixed-seed k-means (cluster centres only; the scored "interesting view" picker is for the library, section 12).
4. **Authored**: `info_poi`, `map_marker`/`map_label`, `point_camera`, `info_observer_point`, `info_target` named `preview_*`.
5. **Custom**: `-camera "x y z pitch yaw [fov]"` or `-look_at <targetname|classname>`.
6. **Problem cameras**: one per top-ranked flagged item (snag cluster, leak exit, unreachable area), aimed at it.
7. **Walkthrough strip** (phase 6): ~8 frames along the nav path from each team's spawn to each objective, laid out as one strip image, to judge flow from the player's view.

Each view records the entities visible in its ID buffer.

## 8. Checks

`PreviewReport` (`index.json`) aggregates; severity uses `CompileDiagnostic` codes (`PRV...`) like `ssmap check`.

- **Gathered from existing code**: compile diagnostics and stage timings, leaks (`LeakReport.Path` polyline plus a camera at the exit), `BspValidator.CheckAsync` results, nav3d floor problems.
- **Missing assets** (sections 4 and 5) with users.
- **Lighting stats**: luxel histogram, black (< 0.02) and clipped (≥ 1.0) fractions, darkest and blown faces with centres, `light_environment` present, per-camera percentiles, luxel size stats.
- **Props**: floating (all downward traces from hull bottom > 8 units), clipping into world (`ToolBspTree.EnumerateLeavesInBox` + `LeafBrushTrace`), and **collision/visual mismatch** from the asset db's mesh vs hull bounds or the model's `.phy`: invisible walls (collision sticks out of the visible mesh by more than a few units) and solid-looking props players fall through (no collision).
- **Entity I/O**: outputs parsed with either separator; dangling targets (excluding `!self`, `!activator`, `!caller`, `!player`, `!pvsplayer`, `!picker`, wildcards); unreferenced names; triggers without outputs; links drawn on the overview.
- **Design conformance**: when the map came from a room level (`LevelYaml`) or carries RoomContracts entities, check each planned room exists in the BSP, is reachable from the right spawns (section 9), and connects to the rooms the design says it connects to, with door widths passing for the profile's hull. This says whether the map does what Claude meant, which a picture cannot.
- **Entity summary**: class counts, spawns per team, `env_cubemap` count, `sky_camera`, worldspawn keys.
- **Playability**: the movement report of section 9, folded in.

## 9. Player movement and reachability (TF2 first)

The question to answer: can a real player get everywhere the map intends, get back out again, and move through the air (rocket, sticky and grenade jumps, pyro airblast, scout double jumps, demo charges) without getting stuck or catching on odd geometry? A voxel walk alone cannot answer that; it needs the game's movement rules run against the game's collision.

### 9.1 Movement profiles (`MapTools/Movement/MovementProfile.cs`)

A data record per game and class, picked from the mounted game (`tf`, `hl2mp`, `cstrike`, ...) or `-profile`:

- hull (TF2: 48×48×82 standing, 62 ducked; HL2: 32×32×72, 36 ducked), eye heights, step height 18, walkable slope (ground when normal.z ≥ 0.7), gravity 800, accelerate, air accelerate, friction, stop speed, max velocity, jump impulse, crouch-jump behaviour, per-class max speeds (TF2 scout 400 ... heavy 230), scout double jump, demo charge, and the launch models in 9.5.
- Starting values come from the TF2 game code in the 2025 Source SDK release and are pinned by the calibration map (9.6), so a wrong constant fails a test rather than misleading Claude.
- Nav3d gets matching walkers: `new Nav3dWalker(48, 82, 62, Nav3dClipClass.Player, Nav3dFit.Centre)` for TF2, with `StepHeight`/`JumpHeight` from the profile.

### 9.2 Hull traces against the real collision (`MapTools/Movement/HullTraceWorld.cs`)

A swept axis-aligned box against everything that blocks a player, as the engine sees it:

- **World and brush entities**: BSP brushes and brush sides (`DBrushSide`, including the bevel sides vbsp adds) with planes pushed out by the box, through the BSP tree to the leaves' brush lists. This mirrors how the engine traces players against the world, bevels included, which is exactly where edge snags come from.
- **Contents**: solid, `playerclip`, grates, team-only blockers (TF2 `func_respawnroomvisualizer` blocks the other team), `func_door`/`func_brush` in their closed or open state as a parameter (setup gates closed or open), `trigger_hurt` and kill volumes as hazards rather than walls.
- **Displacements**: their collision triangles (`DispTri` tags tell which are walkable or buildable), swept box against triangles.
- **Static and dynamic props**: convexes from each model's `.phy` (`PhyFile`, cooked convex points and planes as in `Phys/Managed/`), placed with `InstanceTransform`, swept with GJK-based conservative advancement. Props with `solid 0` or no collision are skipped (and reported by section 8 if they look solid).
- Acceleration structure: a static BVH over brush, displacement and prop bounds, built once per map and shared read-only by every simulation thread.
- Optional cross-check: the native vphysics path (`-cooker native`) for prop traces, used in calibration to measure how often the managed sweep disagrees on grazing contact.

### 9.3 Movement simulator (`MapTools/Movement/PlayerMoveSim.cs`)

A deterministic re-implementation of Source's player movement at 66 ticks per second: ground categorisation (normal.z ≥ 0.7 within a short downward trace), friction, ground and air acceleration, step up and step down (18 units), the slide move that clips velocity against up to four contact planes per tick and handles creases between two planes, jumping and crouch-jumping, ducking in the air (which raises the feet), gravity, base velocity, max velocity clamp, and the "stuck" test (hull starting in solid). Inputs per tick are wish direction, buttons and view angles; outputs are the state plus a contact log (plane, collider id, speed lost) that the analyses read.

### 9.4 Ground reachability (`MapTools/Movement/Reachability.cs`)

1. Build nav3d with the profile's walker.
2. Turn its walkable columns and links into a **directed** graph; validate every link by simulating it (walk from A to B; run up and jump or crouch-jump for jump links; fall for drops), dropping links the simulation fails.
3. Movement tiers, each adding edges: `walk`, `jump` (incl. crouch jump), `double_jump` (scout), `explosive` (soldier rocket, demo sticky and grenade jumps, from 9.5's launch sets), `airblast` (pushed by a pyro).
4. Report per tier and per team (team blockers on, setup gates closed and open):
   - **Intended play area**: everything reachable from the team's spawns by `walk`+`jump`.
   - **Unreachable islands**: walkable floor nobody reaches.
   - **One-way traps**: areas a player can enter but not leave except by dying, found as strongly connected components of the directed graph with no path back to a spawn.
   - **Special-movement-only areas**: reachable only with `double_jump` or `explosive`. They may be deliberate (TF2 maps reward rocket jumpers) or exploits; listed so Claude decides.
   - **Out-of-bounds exposure**: surfaces reachable with `explosive` that are outside the intended area (roofs, skybox edges, the tops of map borders). These usually need `playerclip`.
   - **Objective and item access**: control points, flags, carts, health and ammo reachable by each team, with walking-path travel times per class speed. For symmetric maps, a time imbalance between teams is flagged.

### 9.5 Airborne fuzzing (`MapTools/Movement/AirborneFuzzer.cs`)

Seeded Monte-Carlo trajectories through the simulator, to find where flying players get stuck or snag:

- **Launch sets** (data in the profile): rocket jumps (rocket at the feet or a wall at sampled aim angles, with and without crouch), sticky and grenade jumps (one or more stickies, sampled positions), pyro airblast (push vectors from sampled pyro positions onto airborne and grounded players), scout double jump and knockback, demo charge (horizontal, ground and air). Launch velocities come from the game's blast and knockback rules, calibrated by 9.6.
- **Start points**: sampled on intended walkable floor, weighted toward objectives, chokepoints and high-traffic paths (nav betweenness).
- **Air control**: no input, strafing toward a nav target, and seeded random strafing, so common player behaviour is covered.
- **Events recorded per trajectory**:
  - `stuck`: the hull ends up in solid, or the player is airborne or sliding with no position change for N ticks (wedged in a V between two steep surfaces).
  - `snag`: a large speed loss from a contact on a small feature: a lip lower than the step height caught mid-air, a protrusion or prop edge only a few units deep, a brush seam on a slope that stops a sliding player, a displacement triangle edge. Classified by collider (brush, displacement, prop model).
  - `ceiling_bonk` on low ceilings across common jump paths.
  - `slide_trap`: landing on unwalkable slope (normal.z < 0.7) in a pit with no exit trajectory.
  - `hazard` (entering `trigger_hurt` or a kill volume) and `out_of_bounds`.
- **Output**: events clustered (fixed-parameter DBSCAN) into numbered spots, each with count, rate, collider, a representative trajectory and a suggested fix class ("clip brush over the lip", "bevel the edge", "move the prop or give it a simpler collision model", "playerclip this roof"). Overlays draw trajectories and spots on the overview and on problem cameras.
- **Budgets**: `-quick` (about 10k trajectories, seconds to tens of seconds, for the edit loop) and `-deep` (hundreds of thousands, minutes, before calling a map done). Trajectories run in parallel on `CompilePool` against the shared read-only `HullTraceWorld`; every run with the same seed produces the same report.

### 9.6 Calibration against the real game

- **Movement calibration map** generated by MapGen: lips of 1, 2, 4, 8, 16, 17, 18 and 19 units; gaps of 47, 48 and 49 units; slopes of 44, 45, 46 and 50 degrees; ceilings of 61, 62, 63, 81, 82 and 83 units; V-wedges at several angles; props with collision hulls larger and smaller than their mesh; displacement seams; a rocket-jump tower of known height. Each feature's expected outcome is recorded once from the real game (TF2 on a local server with `setpos`/`setang`, or a SourceMod plugin that replays the launch set and logs end positions; HL2MP through the SourceSharp mod) and committed as fixture data.
- Facts assert the simulator reproduces those outcomes; a mismatch is a bug in the simulator or the profile, not a tolerance to widen.
- **Done:** the `explosive` tier: blast-only areas (flight landings spread along walk edges) with markers, and blast-jump escapes per trap from targeted flights; the rocket jump's push range comes from the calibration.
- **Done (first pass):** `MovementCalibration` in MapTools (not MapGen: the CLI writes the map, so a third-party host can too) with 35 lanes (steps, slopes, jump and crouch-jump ledges, tunnels, ceilings, gaps, jump peaks, rocket-jump launches), `ssmap movecal write|compare`, `tools/preview-calibrate/movement_calibrate.py`, and a recorded TF2 soldier run as a fixture. Still to add: V-wedges, prop hulls, displacement seams, sticky and grenade launches, the scout's double jump and HL2MP.
- Known shipped TF2 maps with community-known snag spots go into a "should find" list for `-deep` runs (skipped when the game content is absent).

## 10. Interface for Claude

```
ssmap preview <map.bsp> [-game <dir>] [-out <dir>] [-views auto|overview|spawns|rooms|problems|all]
     [-camera "x y z pitch yaw [fov]"]... [-look_at <name>] [-size WxH] [-sheet]
     [-overlay entities,leak,reach,snags,io,lightmap,missing,tools,none]
     [-movement off|quick|deep] [-profile tf2|hl2mp|...] [-assets <db dir>]
     [-baseline <dir>] [-watch] [-threads n] [-json-only]

ssmap movement <map.bsp> [-game <dir>] [-profile <name>] [-class all|scout,soldier,...]
     [-tiers walk,jump,double_jump,explosive,airblast] [-quick|-deep] [-seed n] [-out <dir>]
```

- Output: `<out>/index.json`, `<out>/sheet.png` (default; 1536×1024, 3×2 labelled 512 px tiles, about 2k vision tokens), `<out>/views/*.png` on request (1024×768), `<out>/movement.json` plus `reach.png` and `snags.png` overlays, `<out>/diff/` with `-baseline`.
- `-baseline <dir>` re-renders the previous run's cameras and reruns movement with the same seed, then writes heat-diff images and `diff.json` (changed pixels, new and resolved diagnostics, snag spots fixed or introduced).
- Claude's loop: edit, `ssmap all` (fast settings), `ssmap preview map.bsp -movement quick -baseline map.preview.prev`, read the sheet and the numbered findings, fix, repeat; `-movement deep` before finishing.
- MCP server: unscheduled; if built, it wraps the same commands and keeps sessions in a bounded LRU.

## 11. Testing and calibration

- Tests mirror folders: `Tests/MapPreview/...`, `Tests/MapTools/Movement/...`, `Tests/MapTools/MapFacts/...`, `Tests/AssetDb/Maps/...`. MapPreview added to the three rule tests.
- **Renderer**: rasteriser edge, tie, clipping and perspective rules; lightmap UVs against `FaceSampleBuilder`/`LightmapSamplePositions`; each material branch over `SyntheticContent.Build()`; missing asset handling; skins; prop transforms; camera planner; markers and labels laid out without overlap; sheet layout; diff stats; JSON round-trip; PNG round-trip.
- **Goldens**: exact SHA-256 only for small synthetic scenes. Sandbox-map renders are checked by statistics (coverage, luminance percentiles, visible entities), so renderer improvements do not churn digests; real regressions still fail.
- **Renderer calibration (one-time, then on renderer changes)**: once the reference library has shots of shipped maps, take in-game screenshots from the same camera positions (a small committed list of positions per map, captured by hand or a console script) and compare exposure, colour balance, missing surfaces and prop placement. Fix gaps before relying on the renderer; the comparison images stay in the asset db, not the repo.
- **Movement**: hull traces against brute-force tests on synthetic brushes (bevels included), displacement triangles and convexes; each simulator branch (ground check, step up/down, four-plane clip and crease, duck-jump, max velocity, stuck test); reachability tiers and SCC traps on hand-built graphs; fuzzer determinism and event classification on synthetic snags; calibration map outcomes (9.6); a timing guard for `-quick` on the sandbox map.
- **Service rules**: render and simulate N times in one process and assert bounded memory; a cancelled run leaves no half-written files.
- CLI commands tested through `RunAsync(IFileSystem, args, TextWriter, token)` with `InMemoryFileSystem`.

## 12. Reference library of shipped maps, as part of `ssassets`

A searchable corpus of facts and snapshots from maps that ship (HL2, HL2:DM, TF2, CS:S, plus folders of community BSPs) for ideas on composition, scale, lighting, prop dressing and movement routes. Not a source of geometry to copy: the database stores statistics and images, never brushes, and nothing emits VMF/BSP/OBJ from an ingested map.

### 12.1 Two stages

- **Stage A, facts (no renderer; can start now).** Everything in 12.3 except shots, plus a floor plan image (`LevelMapBuilder.FromCompile` polygons filled into `RgbaImage`, spawns and objectives marked with `BitmapFont`), and, once section 9 exists, a movement summary (reachable area, special-movement-only areas, travel times) that shows how shipped maps use rocket-jump routes and height.
- **Stage B, shots (needs the renderer).** `ssassets` references `MapPreview` and renders the standard shot set per map.

### 12.2 CLI

```
ssassets import --game <dir> --out <dir> [existing options]
                [--maps none|all|<glob>] [--map-dir <dir>]... [--shots none|standard|full] [--shot-size <WxH>]
```

Enumeration via `content.EnumerateAsync(VPath.Create("maps"), "*.bsp")` over the existing game mount (loose and VPK maps alike), sorted ordinal; `--map-dir` folders mounted behind the game. The importer's existing identity, skip, `--force`, `ImportIssue`, `remove` and `--threads` rules apply; `games` gains a `maps` count.

### 12.3 Schema (`Database/AssetDatabase.cs`)

```
maps(id, game_id, path, name, sha256, map_checksum, bsp_version, file_bytes, bounds, size_x/y/z,
     faces, brushes, displacements, leaves, clusters, areas, areaportals, doors,
     walkable_components, walkable_area_m2, floor_levels, has_water, has_3d_skybox, skyname,
     sun_pitch, sun_yaw, sun_colour, sun_brightness, ambient_colour, lights, spot_lights, light_styles,
     hdr, cubemaps, lightmap_mean, lightmap_black_fraction, lightmap_clipped_fraction, avg_luxel_size,
     static_props, dynamic_props, detail_props, sky_area_share,
     reach_walk_m2, reach_explosive_only_m2, max_height_gain, plan_image, sheet_image, source)
map_materials(map_id, material_path, material_id NULL, face_count, area)
map_models(map_id, model_path, model_id NULL, kind, count, skins)
map_entities(map_id, classname, count)
map_spawns(map_id, classname, team, x, y, z, yaw)
map_objectives(map_id, classname, targetname, x, y, z, travel_time_red, travel_time_blu)
map_ratios(map_id, name, value)          -- material category shares, prop family counts, sky share, ...
shots(id, map_id, kind, image, width, height, x, y, z, pitch, yaw, fov, ortho_extent, view_matrix,
      leaf, cluster, area, nav_component, sky_share, depth_near, depth_median, depth_far,
      luminance_mean, luminance_p5, luminance_p95, dominant_colours, why)
shot_materials(shot_id, material_path, pixel_share)
shot_models(shot_id, model_path, count)
shot_entities(shot_id, classname, targetname, x, y, z, screen_box)
```

- Links by path with nullable ids resolved after import, so a map imported before its game's textures gains the joins later.
- **Tags are SQL views over `map_ratios` and the link tables**, e.g. `tag_industrial` as metal plus concrete share above 0.5, `tag_catwalks` from prop families and grate materials, `tag_control_point` from objectives, `tag_rocket_routes` from `reach_explosive_only_m2`. Tuning a tag is editing a view (a schema migration in `AssetDatabase.cs`), with no re-import. A `map_tags` view unions them with the ratio that produced each tag.
- To compare Claude's own map with the library under the same tags, import its BSP with `--map-dir <work dir>` into the same database under a `wip` name.

### 12.4 Shots (stage B)

Standard set: `overview-top`, `overview-iso`, one `spawn-*` per team, up to 6 `area-*` views, one `sky` view; `full` adds cardinal spawn views; plus a 3×2 `sheet` per map. The area picker (`MapPreview/Library/InterestPicker.cs`) scores nav-cluster candidates by visible material diversity and prop count (128×96 ID pre-render), open volume, vertical variety, gameplay entities in view and distance from chosen views, with a fixed seed. Clean images; `plan.png` keeps markers and a scale bar. These shots double as the renderer calibration positions (section 11).

### 12.5 Where the code lives

`src/SourceSharp.MapTools/MapFacts/MapFacts.cs` (statistics and ratios from a `BspData` and mount; shared with `ssmap`), `src/SourceSharp.MapPreview/Library/{StandardShotSet,InterestPicker}.cs` (stage B), `src/SourceSharp.AssetDb/Maps/{MapImporter,MapPlan}.cs`, schema and view definitions in `Database/AssetDatabase.cs`, a maps section with worked queries in `Database/AgentReadme.cs`.

### 12.6 Querying

`ssassets query --out <db> "<sql>"`, with worked examples in the agent README:

```sql
-- industrial interiors with catwalks
SELECT m.name, m.sheet_image FROM maps m
JOIN tag_industrial t1 ON t1.map_id = m.id
JOIN tag_catwalks t2 ON t2.map_id = m.id
JOIN tag_interior t3 ON t3.map_id = m.id;

-- what a TF2 control point area looks like and what it is dressed with
SELECT s.image, sm.material_path, sm.pixel_share FROM shots s
JOIN shot_entities e ON e.shot_id = s.id AND e.classname = 'team_control_point'
JOIN shot_materials sm ON sm.shot_id = s.id ORDER BY sm.pixel_share DESC;

-- how much of a typical TF2 map is rocket-jump-only
SELECT m.name, m.reach_explosive_only_m2 / m.reach_walk_m2 FROM maps m
JOIN games g ON g.id = m.game_id WHERE g.name = 'tf';
```

The README states the intended use (scale, composition, lighting, prop dressing, movement routes) and the guard rails: no geometry stored, distributions not placements, images stay in the local database, which is never committed.

### 12.7 Tests

`MapFacts` counts and ratios against the sandbox BSP and hand-built data; each tag view against seeded rows; import into an `InMemoryFileSystem` database (rows, links, skip on unchanged sha, `--force`, corrupt BSP as `ImportIssue`, late `material_id` resolution, `remove`, cancellation, identical bytes on re-run); stage B shot set and picker determinism.

## 13. Phases

**Phase 0: foundations.** Move `PngEncoder`, `RgbaImage`, `BitmapFont`, `ImageStats` to `MapFormats/Imaging/`; add `StudioMeshReader`; create `SourceSharp.MapPreview` and add it to the solution and rule tests. AssetDb's image bytes must not change.

**Phase 1: BSP preview MVP.** Scene builder (world, brush models, displacements, props as hull boxes), phase 1 materials (optionally from `-assets`), deferred-shading rasteriser at output size, overview/isometric/spawn cameras, scale reference, numbered markers, contact sheet, `index.json` aggregating compile, leak, `ssmap check`, missing assets and lighting stats, `-watch`, `ssmap preview`. Measure fast compile and render times (the VMF gate in 3.3). Risks: lightmap UVs on displacements and offset faces; texture memory; render speed on TF2-sized maps.

**Library stage A (parallel with phases 1-2; can start now).** `MapFacts`, `ssassets --maps`, schema, tag views, floor plans, README queries.

**Phase 2: movement core.** `MovementProfile` (TF2, HL2MP), `HullTraceWorld` (brushes with bevels, contents and team blockers, displacements, prop convexes), `PlayerMoveSim`, the MapGen calibration map with real-game outcomes, ground reachability (`walk`, `jump`, `double_jump`), directed SCC traps, per-team access and travel times, `reach` overlay, `ssmap movement` and `-movement quick`. Risks: prop sweep accuracy on grazing contact (cross-check with native vphysics); getting real-game calibration data (needs a local TF2 server or a person in the loop once).

**Phase 3: models, props and design checks.** Real prop meshes with vhv/ambient lighting, prop floating/clipping and collision/visual mismatch, entity I/O, design conformance against room levels, room and problem cameras, labels.

**Phase 4: airborne fuzzing.** Launch sets (rocket, sticky, grenade, airblast, charge, knockback), `explosive`/`airblast` tiers, stuck/snag/bonk/slide-trap/out-of-bounds events, clustering into numbered spots with fix classes, `snags` overlay, `-movement deep`, the "should find" list on shipped TF2 maps. Risks: launch velocity calibration; run time of deep runs (parallel, bounded, seeded).

**Phase 5: library stage B and renderer calibration.** Shots via MapPreview, interest picker, movement summary in library rows, in-game screenshot comparison and fixes.

**Phase 6: iteration loop.** `-baseline` image, report and movement diffs, walkthrough strips, custom cameras and `-look_at`, memory ceiling facts.

**Unscheduled until a measured need**: VMF direct path (3.3), deferred materials (4), MCP server, Vulkan rasteriser.

## Critical files

- `src/SourceSharp.AssetDb/Models/ModelRenderer.cs` and `Imaging/*`: rasteriser rules, triangle walk and imaging helpers to move.
- `src/SourceSharp.MapTools/Rad/Ambient/AmbientScene.cs`, `Disp/DispLightingLoader.cs`, `Rad/Light/FaceSampleBuilder.cs`: reading faces, lightmaps and displacements from a BSP.
- `src/SourceSharp.MapTools/Nav/LevelNavFromBsp.cs`, `src/SourceSharp.MapFormats/Nav/Nav3dWalk.cs`, `Nav3dReader.cs`, `MapTools/Nav/NavSettings.cs`: walkers, step/jump heights, jump links, components.
- `src/SourceSharp.MapFormats/Bsp/Structs/BspStructs.cs` (`DBrushSide`, `DPhysModel`), `DispStructs.cs` (`DispTri`), `src/SourceSharp.MapFormats/Assets/PhyFile.cs`, `src/SourceSharp.MapTools/Phys/Managed/*`: the collision the hull traces are built from.
- `src/SourceSharp.MapTools/Rooms/LevelYaml.cs`, `LevelMapBuilder.cs`, `PlayerHull.cs`: design intent, floor plans, and the HL2-only hull the profiles replace.
- `src/SourceSharp.MapCompile/Program.cs` with `Map2dCommand.cs`/`Nav3dCommand.cs`: verb pattern and game mounting.
- `src/SourceSharp.MapTools/Compile/MapCompiler.cs`, `src/SourceSharp.Tests/MapTools/LibraryRuleTests.cs`.
