//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;
using System.Text;

namespace SourceSharp.AssetDb.Database;

/// <summary>
/// Writes the README that sits beside the database: what is in it, how to
/// query it and how to read the pictures, for an agent that has only the
/// directory.
/// </summary>
/// <remarks>
/// An agent pointed at the output directory reads this first. It therefore
/// carries the conventions the images depend on (which way a view looks,
/// what the grid means) and query recipes for the common map-building
/// questions, not just a table list.
/// </remarks>
public static class AgentReadme
{
    /// <summary>Builds the README text.</summary>
    /// <param name="games">The games the database holds.</param>
    /// <returns>Markdown, newline-terminated.</returns>
    public static string Build(IReadOnlyList<GameSummary> games)
    {
        ArgumentNullException.ThrowIfNull(games);

        StringBuilder text = new();
        text.Append(Header);
        text.AppendLine();
        text.AppendLine("## Games in this database");
        text.AppendLine();
        if (games.Count == 0)
        {
            text.AppendLine("None yet. Import one with `ssassets import`.");
        }
        else
        {
            text.AppendLine("| game | title | textures | materials | models | maps read | issues | imported (UTC) |");
            text.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |");
            foreach (GameSummary game in games)
            {
                text.Append(CultureInfo.InvariantCulture, $"| `{game.Name}` | {game.Title ?? string.Empty} ")
                    .Append(CultureInfo.InvariantCulture, $"| {game.Textures} | {game.Materials} | {game.Models} | {game.Maps} ")
                    .Append(CultureInfo.InvariantCulture, $"| {game.Issues} | {game.ImportedUtc} |")
                    .AppendLine();
            }
        }

        text.AppendLine();
        text.AppendLine(Body);
        return text.ToString();
    }

    /// <summary>
    /// Builds the CLAUDE.md: the short, imperative workflow for an agent
    /// choosing content, which defers to the README for the reference.
    /// </summary>
    /// <param name="games">The games the database holds.</param>
    /// <returns>Markdown, newline-terminated.</returns>
    /// <remarks>
    /// Kept apart from the README because the two are read differently:
    /// CLAUDE.md is loaded into every session started here, so it carries
    /// only the steps and the rules that prevent the common mistakes, and
    /// points at the README for the schema and the image conventions.
    /// </remarks>
    public static string BuildClaude(IReadOnlyList<GameSummary> games)
    {
        ArgumentNullException.ThrowIfNull(games);

        StringBuilder text = new();
        text.Append(ClaudeHeader);
        text.AppendLine();
        text.Append("Games here: ");
        text.AppendLine(games.Count == 0
            ? "none yet."
            : string.Join(", ", games.Select(g => $"`{g.Name}`" + (g.Title is null ? string.Empty : $" ({g.Title})"))) + ".");
        text.AppendLine();
        text.AppendLine(ClaudeBody);
        return text.ToString();
    }

    private const string ClaudeHeader = """
        # Using this asset database

        This directory is a picture-and-metadata catalogue of Source engine
        game content, for choosing materials and props when designing a map.
        `README.md` here is the full reference (schema, view conventions,
        more queries); read it once before relying on details.

        """;

    private const string ClaudeBody = """
        ## Workflow

        1. **Query, don't browse.** Search `assets.db` with SQL, filtered to
           the game the map is for:
           ```sh
           sqlite3 -header -column assets.db "SELECT material, surface_prop, average_colour, image FROM material_view WHERE game = 'tf' AND usage = 'brush' AND material LIKE '%wood%' LIMIT 40"
           ```
           Without `sqlite3`, use `ssassets query --out <this dir> "<sql>"`
           (read-only). Never write to the database by hand.
        2. **Narrow by text first, then look.** Filter on `usage`,
           `category`, `surface_prop`, `keywords`, `average_colour` and, for
           props, `size_x/y/z` and `static_prop`, down to a short list; then
           open the shortlisted images (paths are relative to this directory)
           with the Read tool. Do not open hundreds of images. For a
           material, open `preview` when it has one (lit, shows relief),
           otherwise `image`.
        3. **Lean on what the game's designers used.** `maps_used` ranks
           materials and props by how many shipped maps use them; the
           `material_usage` / `model_usage` views (README has co-occurrence
           queries) show what goes together. A set of materials and props
           that appears together in shipped maps is a set that looks right.
        4. **For a prop, open `image_sheet` first**: one image with all six
           labelled views over a unit grid. Open a single view only when a
           detail matters.
        5. **Write what the map needs, verbatim:**
           - a brush face's material is `material_view.material`
             (e.g. `brick/brickwall001a`);
           - a prop's `model` key is `model_view.model`
             (e.g. `models/props_c17/oildrum001.mdl`).

        ## Rules

        - Brush faces only take `usage = 'brush'` materials (or `tool` ones
          such as `tools/toolsnodraw`); `model` materials are prop skins and
          render wrongly on brushes.
        - Use `static_prop = 1` models for `prop_static`. A non-null
          `prop_data` means the model also works as `prop_physics`.
        - Check `game`: content in one game's rows may not exist in another's.
        - Sizes are in world units relative to the model's origin, which is
          the point the entity's `origin` places (the dark lines in the
          views). `mesh_min_z` near 0 means the prop sits on the floor when
          placed at floor height.
        - Not every model faces +X; check the sheet for its front before
          choosing a yaw.
        - Pick materials that suit each other: compare `average_colour` and
          look at the candidates side by side before committing to a palette.
        """;

    private const string Header = """
        # Source asset database

        Pictures and metadata for every texture, material and prop model of the
        games listed below, built by `ssassets` for choosing what to put in a
        map. Everything is in `assets.db` (SQLite); image paths in it are
        relative to this directory.

        """;

    private const string Body = """
        ## What to use for what

        - **Brush faces** take a *material* name: `material_view.material`,
          written into a VMF side's `material` key in upper or lower case, e.g.
          `BRICK/BRICKWALL001A`. Filter on `usage = 'brush'` for walls, floors
          and ceilings. `image` is its flat base texture (true colour);
          `preview`, when present, is the same texture lit through its bump
          map from the upper left, which shows the relief you see in game.
        - **Props** take a *model* path: `model_view.model`, written verbatim
          into a `prop_static` / `prop_physics` / `prop_dynamic` `model` key,
          e.g. `models/props_c17/oildrum001.mdl`. Use `static_prop = 1` models
          for `prop_static`. `prop_data` non-null means the model can be a
          physics prop.
        - **Textures** are the raw `.vtf` images behind materials. Maps never
          name them directly. `kind` is `color`, `normal` or `ssbump`; bump
          maps (`normal`, `ssbump`) have a row but no image, since their
          pixels are directions, not colours.
        - **What the game's own designers used**: every shipped map was read.
          `maps_used` and `area_used` (square units of faces) in
          `material_view`, `maps_used` and `placements` in `model_view`, and
          per map in `material_usage` / `model_usage`. Prefer well-used
          content and copy combinations that appear together.

        ## Queries

        ```sh
        sqlite3 -header -column assets.db "SELECT ..."
        ```

        ```sql
        -- brick walls in TF2, with pictures
        SELECT material, surface_prop, average_colour, image FROM material_view
        WHERE game = 'tf' AND usage = 'brush' AND material LIKE '%brick%';

        -- floors that sound like metal
        SELECT material, image FROM material_view
        WHERE game = 'hl2' AND usage = 'brush' AND surface_prop LIKE 'metal%';

        -- see-through brush materials: fences, grates, glass
        SELECT material, alpha_test, translucent, image FROM material_view
        WHERE game = 'hl2' AND usage = 'brush' AND (alpha_test = 1 OR translucent = 1);

        -- crates and barrels under 64 units tall that can be prop_static
        SELECT model, size_x, size_y, size_z, image_sheet FROM model_view
        WHERE game = 'hl2' AND static_prop = 1 AND size_z <= 64
          AND (model LIKE '%crate%' OR model LIKE '%barrel%');

        -- the most used brush materials, a safe palette
        SELECT material, maps_used, area_used, preview, image FROM material_view
        WHERE game = 'tf' AND usage = 'brush' ORDER BY maps_used DESC LIMIT 40;

        -- what Valve's maps pair with a material (co-occurrence)
        SELECT b.material, count(*) AS maps_together FROM material_usage a
        JOIN material_usage b ON b.game = a.game AND b.map = a.map AND b.material <> a.material
        WHERE a.game = 'tf' AND a.material = 'brick/brickwall001a' AND b.kind = 'face'
        GROUP BY b.material ORDER BY maps_together DESC LIMIT 25;

        -- props that appear with a material, by how many maps share them
        SELECT p.model, count(DISTINCT p.map) AS maps FROM material_usage m
        JOIN model_usage p ON p.game = m.game AND p.map = m.map
        WHERE m.game = 'tf' AND m.material = 'wood/wood_bridge001'
        GROUP BY p.model ORDER BY maps DESC LIMIT 25;

        -- which skybox a style of map uses
        SELECT name, sky_name FROM maps WHERE game_id = (SELECT id FROM games WHERE name = 'tf') AND name LIKE 'cp_%';

        -- every skin of a model
        SELECT skin, slot, material FROM model_materials mm JOIN models m ON m.id = mm.model_id
        WHERE m.path = 'models/props_c17/oildrum001.mdl' ORDER BY skin, slot;

        -- why something is missing
        SELECT kind, path, reason FROM issues i JOIN games g ON g.id = i.game_id WHERE g.name = 'tf';
        ```

        Hex colours compare as text only for equality; pull rows and compare
        channels in code for "close to" questions.

        ## Reading the model pictures

        Each model has a directory (`models/<path>/`) with one PNG per view and
        `sheet.png`, all six views on one image (front, left, right on the top
        row; back, top, iso below). Every panel is labelled with its view.

        Source's convention is that a model faces **+X**, and the views are
        named from the model's point of view on that assumption. Not every
        model follows it (Team Fortress 2's resupply locker shows its doors in
        the `right` view, so it faces -Y): look at the sheet to see which side
        is the front before choosing a prop's yaw.

        | view | camera at | screen right | screen up |
        | --- | --- | --- | --- |
        | front | +X | +Y | +Z |
        | back | -X | -Y | +Z |
        | left | +Y (the model's left) | -X | +Z |
        | right | -Y (the model's right) | +X | +Z |
        | top | +Z, looking down (Hammer's top view) | +X | +Y |
        | iso | front-right, above | | |

        The five axis views share one scale, so proportions between them are
        true. Behind the model is a grid in world units; its step is printed in
        the panel (`GRID 64`) and stored as `grid_step`. The two darker lines
        cross at the model's **origin**, the point an entity's `origin` places;
        a prop whose origin is at its base sits on the floor when placed at
        floor height. For scale: the Half-Life 2 player hull is 32 x 32 x 72
        units, Team Fortress 2's 48 x 48 x 82.

        Sizes in `model_view` (`size_x/y/z`, `mesh_min_*`, `mesh_max_*`) are
        from the drawn triangles, in units, relative to the origin.

        ## Tables

        | table | one row per |
        | --- | --- |
        | `games` | imported game |
        | `textures` | distinct texture (path + content hash), shared between games |
        | `game_textures` | texture a game can see, with the archive it came from |
        | `materials` | material per game; `texture_id` is its base (or tool) texture, `preview_id` its lit preview |
        | `previews` | distinct lit preview (material + hash of its textures), shared between games |
        | `models` | distinct model, shared between games; image paths per view |
        | `game_models` | model a game can see |
        | `model_materials` | material slot per skin per model per game |
        | `maps` | shipped map read for usage: faces, static props, entities, `sky_name` |
        | `map_materials` | material per map: `kind` `face` (with `count` and `area`), `overlay` or `decal` |
        | `map_models` | model per map: `kind` `static` (prop_static) or `entity` (any entity's model key) |
        | `issues` | file an import passed over, with why |

        `map_materials` also names materials packed inside a map (community
        maps carry their own); those have no row in `materials`, so the views
        that join on `materials` leave them out.

        `materials.parameters` is a JSON object of the material's top-level
        values (`json_extract(parameters, '$."$basetexture"')`); `materials.vmt`
        is the file itself. `models.keyvalues` is the model's embedded
        keyvalue text (`prop_data` and the like).

        Rebuild or add a game with `ssassets import --game <dir with gameinfo.txt> --out <this dir>`.
        """;
}
