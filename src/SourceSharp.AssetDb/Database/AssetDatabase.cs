//========= Copyright Valve Corporation, All rights reserved. ============//
//
// Inspired by and based on the Half-Life 2 Source SDK 2013 by Valve:
// https://github.com/ValveSoftware/source-sdk-2013
//
//=============================================================================//

using System.Globalization;

using Microsoft.Data.Sqlite;

using SourceSharp.AssetDb.Materials;
using SourceSharp.AssetDb.Models;
using SourceSharp.AssetDb.Textures;
using SourceSharp.MapFormats.Geometry;

namespace SourceSharp.AssetDb.Database;

/// <summary>One game as the database records it.</summary>
/// <param name="Name">The short name the game was imported under (<c>hl2</c>, <c>tf</c>).</param>
/// <param name="Title">The <c>game</c> title from its gameinfo, when it has one.</param>
/// <param name="Directory">The directory the gameinfo was read from.</param>
/// <param name="ImportedUtc">When the import finished, ISO 8601, UTC.</param>
/// <param name="Textures">How many textures the game can see.</param>
/// <param name="Materials">How many materials.</param>
/// <param name="Models">How many models were exported.</param>
/// <param name="Issues">How many inputs were passed over.</param>
public sealed record GameSummary(
    string Name,
    string? Title,
    string Directory,
    string ImportedUtc,
    int Textures,
    int Materials,
    int Models,
    int Issues);

/// <summary>Everything one game's import found, ready to store.</summary>
public sealed record GameImport
{
    /// <summary>Gets the short name the game is stored under.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the gameinfo title, when it has one.</summary>
    public string? Title { get; init; }

    /// <summary>Gets the directory the gameinfo was read from.</summary>
    public required string Directory { get; init; }

    /// <summary>Gets when the import finished, ISO 8601, UTC.</summary>
    public required string ImportedUtc { get; init; }

    /// <summary>Gets the textures the game can see.</summary>
    public required IReadOnlyList<TextureRecord> Textures { get; init; }

    /// <summary>Gets the game's materials.</summary>
    public required IReadOnlyList<MaterialRecord> Materials { get; init; }

    /// <summary>Gets the models exported for the game.</summary>
    public required IReadOnlyList<ModelRecord> Models { get; init; }

    /// <summary>Gets what was passed over, and why.</summary>
    public required IReadOnlyList<ImportIssue> Issues { get; init; }
}

/// <summary>
/// The asset database: one SQLite file beside the exported images, holding
/// every game imported into it.
/// </summary>
/// <remarks>
/// <para>
/// The schema is written for a reader that knows SQL and nothing else about
/// Source: the <c>material_view</c>, <c>model_view</c> and
/// <c>texture_view</c> views join everything a map author asks about into one
/// row per asset per game, image paths included, and the tables underneath
/// are there for anything the views do not answer. The README written beside
/// the database documents both.
/// </para>
/// <para>
/// Textures and models are stored once per distinct content (path plus
/// hash) and linked to every game that sees them; materials are stored per
/// game, because what a material resolves to depends on the game's search
/// paths. Re-importing a game replaces its links, materials and issues in
/// one transaction, so a failed import leaves the previous one intact.
/// </para>
/// <para>
/// One connection, opened without pooling, so disposing the database closes
/// the file: a host that imports many times must not accumulate handles.
/// </para>
/// </remarks>
public sealed class AssetDatabase : IDisposable
{
    /// <summary>The schema version this code writes, stored in <c>PRAGMA user_version</c>.</summary>
    public const int SchemaVersion = 1;

    /// <summary>The database's file name inside the output directory.</summary>
    public const string FileName = "assets.db";

    private readonly SqliteConnection _connection;

    private AssetDatabase(SqliteConnection connection) => _connection = connection;

    /// <summary>
    /// Opens (creating when absent) a database and brings its schema up to
    /// <see cref="SchemaVersion"/>.
    /// </summary>
    /// <param name="dataSource">A file path, or <c>:memory:</c> for a private in-memory database.</param>
    /// <returns>The open database; dispose it.</returns>
    /// <exception cref="InvalidOperationException">The file was written by a newer, or unknown, schema.</exception>
    public static AssetDatabase Open(string dataSource)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataSource);

        SqliteConnectionStringBuilder builder = new() { DataSource = dataSource, Pooling = false };
        SqliteConnection connection = new(builder.ToString());
        try
        {
            connection.Open();
            AssetDatabase database = new(connection);
            database.Migrate();
            return database;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens an existing database for reading only: no schema change, and
    /// any statement that would write fails.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <returns>The open database; dispose it.</returns>
    /// <exception cref="InvalidOperationException">The file has another schema version.</exception>
    /// <remarks>
    /// What the <c>list</c> and <c>query</c> verbs use: an agent handed a
    /// SQL prompt cannot damage the database by mistake.
    /// </remarks>
    public static AssetDatabase OpenReadOnly(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        SqliteConnection connection = new(builder.ToString());
        try
        {
            connection.Open();
            using SqliteCommand version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            long current = (long)version.ExecuteScalar()!;
            if (current != SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"the asset database has schema version {current}; this tool reads version {SchemaVersion}");
            }

            return new AssetDatabase(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => _connection.Dispose();

    /// <summary>Every texture the database holds, for reuse by the next import.</summary>
    /// <returns>The records, with an empty <see cref="TextureRecord.Source"/>.</returns>
    public IReadOnlyList<TextureRecord> LoadTextures()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT path, sha256, width, height, format, mips, frames, flags,
                   reflectivity_r, reflectivity_g, reflectivity_b, average_colour, has_alpha,
                   image, image_width, image_height
            FROM textures ORDER BY path, sha256
            """;
        List<TextureRecord> records = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new TextureRecord
            {
                Path = reader.GetString(0),
                Sha256 = reader.GetString(1),
                Width = reader.GetInt32(2),
                Height = reader.GetInt32(3),
                Format = reader.GetString(4),
                Mips = reader.GetInt32(5),
                Frames = reader.GetInt32(6),
                Flags = reader.GetString(7),
                Reflectivity = (reader.GetFloat(8), reader.GetFloat(9), reader.GetFloat(10)),
                AverageColour = reader.GetString(11),
                HasAlpha = reader.GetInt32(12) != 0,
                Image = reader.GetString(13),
                ImageWidth = reader.GetInt32(14),
                ImageHeight = reader.GetInt32(15),
                Source = string.Empty,
            });
        }

        return records;
    }

    /// <summary>Every model the database holds, as much as reuse needs.</summary>
    /// <returns>The path, hash and image directory of each.</returns>
    public IReadOnlyList<KnownModel> LoadModels()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT path, sha256, image_dir FROM models ORDER BY path, sha256";
        List<KnownModel> records = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new KnownModel(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return records;
    }

    /// <summary>
    /// Stores one game's import, replacing whatever an earlier import of the
    /// same name stored, in one transaction.
    /// </summary>
    /// <param name="import">What the import found.</param>
    /// <param name="cancellationToken">Cancels the write; the transaction rolls back.</param>
    public void ReplaceGame(GameImport import, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(import);

        using SqliteTransaction transaction = _connection.BeginTransaction();
        long game = UpsertGame(import, transaction);
        foreach (string table in (string[])["game_textures", "materials", "game_models", "model_materials", "issues"])
        {
            Execute(transaction, $"DELETE FROM {table} WHERE game_id = $game", ("$game", game));
        }

        InsertTextures(transaction, game, import.Textures, cancellationToken);
        InsertMaterials(transaction, game, import.Materials, cancellationToken);
        InsertModels(transaction, game, import.Models, cancellationToken);

        using (SqliteCommand issue = Command(transaction, """
            INSERT INTO issues(game_id, kind, path, reason) VALUES ($game, $kind, $path, $reason)
            """))
        {
            foreach (ImportIssue item in import.Issues)
            {
                Bind(issue, ("$game", game), ("$kind", item.Kind), ("$path", item.Path), ("$reason", item.Reason));
                issue.ExecuteNonQuery();
            }
        }

        // The preview texture of each material: its base texture, or its
        // tool texture, among the textures THIS game sees.
        Execute(transaction, """
            UPDATE materials SET texture_id = (
                SELECT t.id FROM game_textures gt JOIN textures t ON t.id = gt.texture_id
                WHERE gt.game_id = materials.game_id
                  AND t.path = coalesce(materials.base_texture, materials.tool_texture))
            WHERE game_id = $game
            """, ("$game", game));

        Execute(transaction, """
            UPDATE games SET
                textures = (SELECT count(*) FROM game_textures WHERE game_id = $game),
                materials = (SELECT count(*) FROM materials WHERE game_id = $game),
                models = (SELECT count(*) FROM game_models WHERE game_id = $game),
                issues = (SELECT count(*) FROM issues WHERE game_id = $game)
            WHERE id = $game
            """, ("$game", game));

        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    /// <summary>Removes a game and everything only it referenced from the tables.</summary>
    /// <param name="name">The game's short name.</param>
    /// <returns>True when the game was there.</returns>
    /// <remarks>Images are not touched; call <see cref="PruneOrphans"/> and delete what it returns.</remarks>
    public bool RemoveGame(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand find = Command(transaction, "SELECT id FROM games WHERE name = $name");
        Bind(find, ("$name", name));
        if (find.ExecuteScalar() is not long game)
        {
            return false;
        }

        foreach (string table in (string[])["game_textures", "materials", "game_models", "model_materials", "issues"])
        {
            Execute(transaction, $"DELETE FROM {table} WHERE game_id = $game", ("$game", game));
        }

        Execute(transaction, "DELETE FROM games WHERE id = $game", ("$game", game));
        transaction.Commit();
        return true;
    }

    /// <summary>
    /// Deletes the texture and model rows no game links to any more, and
    /// returns the image files they owned so the caller can delete those.
    /// </summary>
    /// <returns>The orphaned image paths, relative to the database's directory, sorted.</returns>
    /// <remarks>
    /// An import never deletes rows on its own: a texture that left one game
    /// may still be linked from another. This is the one place rows go.
    /// </remarks>
    public IReadOnlyList<string> PruneOrphans()
    {
        List<string> images = [];
        using SqliteTransaction transaction = _connection.BeginTransaction();

        using (SqliteCommand textures = Command(transaction, """
            SELECT image FROM textures WHERE id NOT IN (SELECT texture_id FROM game_textures)
            """))
        using (SqliteDataReader reader = textures.ExecuteReader())
        {
            while (reader.Read())
            {
                images.Add(reader.GetString(0));
            }
        }

        using (SqliteCommand models = Command(transaction, """
            SELECT image_dir FROM models WHERE id NOT IN (SELECT model_id FROM game_models)
            """))
        using (SqliteDataReader reader = models.ExecuteReader())
        {
            while (reader.Read())
            {
                string directory = reader.GetString(0);
                foreach (ModelView view in Enum.GetValues<ModelView>())
                {
                    images.Add($"{directory}/{view.ToString().ToLowerInvariant()}.png");
                }

                images.Add($"{directory}/{ModelExporter.SheetFileName}");
            }
        }

        Execute(transaction, "UPDATE materials SET texture_id = NULL WHERE texture_id NOT IN (SELECT texture_id FROM game_textures)");
        Execute(transaction, "DELETE FROM textures WHERE id NOT IN (SELECT texture_id FROM game_textures)");
        Execute(transaction, "DELETE FROM models WHERE id NOT IN (SELECT model_id FROM game_models)");
        transaction.Commit();

        images.Sort(StringComparer.Ordinal);
        return images;
    }

    /// <summary>Every game the database holds, by name.</summary>
    /// <returns>The summaries.</returns>
    public IReadOnlyList<GameSummary> Games()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT name, title, directory, imported_utc, textures, materials, models, issues
            FROM games ORDER BY name
            """;
        List<GameSummary> games = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            games.Add(new GameSummary(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                reader.GetInt32(7)));
        }

        return games;
    }

    /// <summary>
    /// Runs one statement and returns its rows as text, for the CLI's
    /// <c>query</c> verb (which opens the database with
    /// <see cref="OpenReadOnly"/>) and for tests.
    /// </summary>
    /// <param name="sql">One SQL statement.</param>
    /// <param name="parameters">Named parameters to bind.</param>
    /// <returns>The column names and the rows, every value as text (null as null).</returns>
    public (IReadOnlyList<string> Columns, IReadOnlyList<string?[]> Rows) Query(
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        ArgumentException.ThrowIfNullOrEmpty(sql);

        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        Bind(command, parameters);
        using SqliteDataReader reader = command.ExecuteReader();
        string[] columns = new string[reader.FieldCount];
        for (int i = 0; i < columns.Length; i++)
        {
            columns[i] = reader.GetName(i);
        }

        List<string?[]> rows = [];
        while (reader.Read())
        {
            string?[] row = new string?[columns.Length];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i)
                    ? null
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            rows.Add(row);
        }

        return (columns, rows);
    }

    private void Migrate()
    {
        using SqliteCommand version = _connection.CreateCommand();
        version.CommandText = "PRAGMA user_version";
        long current = (long)version.ExecuteScalar()!;
        if (current == SchemaVersion)
        {
            return;
        }

        if (current != 0)
        {
            throw new InvalidOperationException(
                $"the asset database has schema version {current}; this tool writes version {SchemaVersion}. "
                + "Build a fresh database in a new directory.");
        }

        using SqliteTransaction transaction = _connection.BeginTransaction();
        Execute(transaction, Schema);
        Execute(transaction, $"PRAGMA user_version = {SchemaVersion}");
        transaction.Commit();
    }

    private long UpsertGame(GameImport import, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(transaction, """
            INSERT INTO games(name, title, directory, imported_utc) VALUES ($name, $title, $directory, $imported)
            ON CONFLICT(name) DO UPDATE SET
                title = excluded.title, directory = excluded.directory, imported_utc = excluded.imported_utc
            RETURNING id
            """);
        Bind(
            command,
            ("$name", import.Name),
            ("$title", import.Title),
            ("$directory", import.Directory),
            ("$imported", import.ImportedUtc));
        return (long)command.ExecuteScalar()!;
    }

    private static void InsertTextures(
        SqliteTransaction transaction,
        long game,
        IReadOnlyList<TextureRecord> textures,
        CancellationToken cancellationToken)
    {
        using SqliteCommand upsert = Command(transaction, """
            INSERT INTO textures(path, sha256, width, height, format, mips, frames, flags,
                reflectivity_r, reflectivity_g, reflectivity_b, average_colour, has_alpha,
                image, image_width, image_height)
            VALUES ($path, $sha, $width, $height, $format, $mips, $frames, $flags,
                $rr, $rg, $rb, $colour, $alpha, $image, $iw, $ih)
            ON CONFLICT(path, sha256) DO UPDATE SET
                width = excluded.width, height = excluded.height, format = excluded.format,
                mips = excluded.mips, frames = excluded.frames, flags = excluded.flags,
                reflectivity_r = excluded.reflectivity_r, reflectivity_g = excluded.reflectivity_g,
                reflectivity_b = excluded.reflectivity_b, average_colour = excluded.average_colour,
                has_alpha = excluded.has_alpha, image = excluded.image,
                image_width = excluded.image_width, image_height = excluded.image_height
            RETURNING id
            """);
        using SqliteCommand link = Command(transaction, """
            INSERT OR REPLACE INTO game_textures(game_id, texture_id, source) VALUES ($game, $texture, $source)
            """);

        foreach (TextureRecord t in textures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bind(
                upsert,
                ("$path", t.Path), ("$sha", t.Sha256), ("$width", t.Width), ("$height", t.Height),
                ("$format", t.Format), ("$mips", t.Mips), ("$frames", t.Frames), ("$flags", t.Flags),
                ("$rr", t.Reflectivity.R), ("$rg", t.Reflectivity.G), ("$rb", t.Reflectivity.B),
                ("$colour", t.AverageColour), ("$alpha", t.HasAlpha), ("$image", t.Image),
                ("$iw", t.ImageWidth), ("$ih", t.ImageHeight));
            long id = (long)upsert.ExecuteScalar()!;
            Bind(link, ("$game", game), ("$texture", id), ("$source", t.Source));
            link.ExecuteNonQuery();
        }
    }

    private static void InsertMaterials(
        SqliteTransaction transaction,
        long game,
        IReadOnlyList<MaterialRecord> materials,
        CancellationToken cancellationToken)
    {
        using SqliteCommand insert = Command(transaction, """
            INSERT OR REPLACE INTO materials(game_id, path, shader, usage, category,
                base_texture, base_texture2, bump_map, detail, env_map, tool_texture,
                surface_prop, surface_prop2, keywords, translucent, alpha_test, self_illum,
                include, parameters, vmt, source)
            VALUES ($game, $path, $shader, $usage, $category,
                $base, $base2, $bump, $detail, $envmap, $tool,
                $surface, $surface2, $keywords, $translucent, $alphatest, $selfillum,
                $include, $parameters, $vmt, $source)
            """);

        foreach (MaterialRecord m in materials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bind(
                insert,
                ("$game", game), ("$path", m.Path), ("$shader", m.Shader), ("$usage", m.Usage),
                ("$category", m.Category), ("$base", m.BaseTexture), ("$base2", m.BaseTexture2),
                ("$bump", m.BumpMap), ("$detail", m.Detail), ("$envmap", m.EnvMap), ("$tool", m.ToolTexture),
                ("$surface", m.SurfaceProp), ("$surface2", m.SurfaceProp2), ("$keywords", m.Keywords),
                ("$translucent", m.Translucent), ("$alphatest", m.AlphaTest), ("$selfillum", m.SelfIllum),
                ("$include", m.Include), ("$parameters", m.Parameters), ("$vmt", m.Vmt), ("$source", m.Source));
            insert.ExecuteNonQuery();
        }
    }

    private static void InsertModels(
        SqliteTransaction transaction,
        long game,
        IReadOnlyList<ModelRecord> models,
        CancellationToken cancellationToken)
    {
        using SqliteCommand upsert = Command(transaction, """
            INSERT INTO models(path, sha256, name, flags, static_prop, surface_prop, mass,
                size_x, size_y, size_z,
                mesh_min_x, mesh_min_y, mesh_min_z, mesh_max_x, mesh_max_y, mesh_max_z,
                hull_min_x, hull_min_y, hull_min_z, hull_max_x, hull_max_y, hull_max_z,
                view_min_x, view_min_y, view_min_z, view_max_x, view_max_y, view_max_z,
                bones, sequences, body_parts, lods, triangles, vertices, skins, has_collision,
                prop_data, keyvalues, grid_step, image_dir,
                image_front, image_back, image_left, image_right, image_top, image_iso, image_sheet)
            VALUES ($path, $sha, $name, $flags, $static, $surface, $mass,
                $sx, $sy, $sz,
                $mnx, $mny, $mnz, $mxx, $mxy, $mxz,
                $hnx, $hny, $hnz, $hxx, $hxy, $hxz,
                $vnx, $vny, $vnz, $vxx, $vxy, $vxz,
                $bones, $sequences, $parts, $lods, $triangles, $vertices, $skins, $collision,
                $propdata, $keyvalues, $grid, $dir,
                $front, $back, $left, $right, $top, $iso, $sheet)
            ON CONFLICT(path, sha256) DO UPDATE SET
                name = excluded.name, flags = excluded.flags, static_prop = excluded.static_prop,
                surface_prop = excluded.surface_prop, mass = excluded.mass,
                size_x = excluded.size_x, size_y = excluded.size_y, size_z = excluded.size_z,
                mesh_min_x = excluded.mesh_min_x, mesh_min_y = excluded.mesh_min_y, mesh_min_z = excluded.mesh_min_z,
                mesh_max_x = excluded.mesh_max_x, mesh_max_y = excluded.mesh_max_y, mesh_max_z = excluded.mesh_max_z,
                hull_min_x = excluded.hull_min_x, hull_min_y = excluded.hull_min_y, hull_min_z = excluded.hull_min_z,
                hull_max_x = excluded.hull_max_x, hull_max_y = excluded.hull_max_y, hull_max_z = excluded.hull_max_z,
                view_min_x = excluded.view_min_x, view_min_y = excluded.view_min_y, view_min_z = excluded.view_min_z,
                view_max_x = excluded.view_max_x, view_max_y = excluded.view_max_y, view_max_z = excluded.view_max_z,
                bones = excluded.bones, sequences = excluded.sequences, body_parts = excluded.body_parts,
                lods = excluded.lods, triangles = excluded.triangles, vertices = excluded.vertices,
                skins = excluded.skins, has_collision = excluded.has_collision, prop_data = excluded.prop_data,
                keyvalues = excluded.keyvalues, grid_step = excluded.grid_step, image_dir = excluded.image_dir,
                image_front = excluded.image_front, image_back = excluded.image_back,
                image_left = excluded.image_left, image_right = excluded.image_right,
                image_top = excluded.image_top, image_iso = excluded.image_iso, image_sheet = excluded.image_sheet
            RETURNING id
            """);
        using SqliteCommand link = Command(transaction, """
            INSERT OR REPLACE INTO game_models(game_id, model_id, source) VALUES ($game, $model, $source)
            """);
        using SqliteCommand material = Command(transaction, """
            INSERT OR REPLACE INTO model_materials(game_id, model_id, skin, slot, texture, material)
            VALUES ($game, $model, $skin, $slot, $texture, $material)
            """);

        foreach (ModelRecord m in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Vec3 size = m.Mesh.Max - m.Mesh.Min;
            Bind(
                upsert,
                ("$path", m.Path), ("$sha", m.Sha256), ("$name", m.Name), ("$flags", m.Flags),
                ("$static", m.IsStaticProp), ("$surface", m.SurfaceProp), ("$mass", m.Mass),
                ("$sx", size.X), ("$sy", size.Y), ("$sz", size.Z),
                ("$mnx", m.Mesh.Min.X), ("$mny", m.Mesh.Min.Y), ("$mnz", m.Mesh.Min.Z),
                ("$mxx", m.Mesh.Max.X), ("$mxy", m.Mesh.Max.Y), ("$mxz", m.Mesh.Max.Z),
                ("$hnx", m.Hull.Min.X), ("$hny", m.Hull.Min.Y), ("$hnz", m.Hull.Min.Z),
                ("$hxx", m.Hull.Max.X), ("$hxy", m.Hull.Max.Y), ("$hxz", m.Hull.Max.Z),
                ("$vnx", m.View.Min.X), ("$vny", m.View.Min.Y), ("$vnz", m.View.Min.Z),
                ("$vxx", m.View.Max.X), ("$vxy", m.View.Max.Y), ("$vxz", m.View.Max.Z),
                ("$bones", m.Bones), ("$sequences", m.Sequences), ("$parts", m.BodyParts), ("$lods", m.Lods),
                ("$triangles", m.Triangles), ("$vertices", m.Vertices), ("$skins", m.Skins),
                ("$collision", m.HasCollision), ("$propdata", m.PropData), ("$keyvalues", m.KeyValues),
                ("$grid", m.GridStep), ("$dir", m.ImageDirectory),
                ("$front", m.ImageFor(ModelView.Front)), ("$back", m.ImageFor(ModelView.Back)),
                ("$left", m.ImageFor(ModelView.Left)), ("$right", m.ImageFor(ModelView.Right)),
                ("$top", m.ImageFor(ModelView.Top)), ("$iso", m.ImageFor(ModelView.Iso)),
                ("$sheet", m.SheetImage));
            long id = (long)upsert.ExecuteScalar()!;
            Bind(link, ("$game", game), ("$model", id), ("$source", m.Source));
            link.ExecuteNonQuery();

            foreach (ModelMaterial row in m.Materials)
            {
                Bind(
                    material,
                    ("$game", game), ("$model", id), ("$skin", row.Skin), ("$slot", row.Slot),
                    ("$texture", row.Texture), ("$material", row.Material));
                material.ExecuteNonQuery();
            }
        }
    }

    private static SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void Execute(SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand command = Command(transaction, sql);
        Bind(command, parameters);
        command.ExecuteNonQuery();
    }

    /// <summary>Binds parameters, replacing any bound before: commands are reused row after row.</summary>
    private static void Bind(SqliteCommand command, params (string Name, object? Value)[] parameters)
    {
        command.Parameters.Clear();
        foreach ((string name, object? value) in parameters)
        {
            object bound = value switch
            {
                null => DBNull.Value,
                bool flag => flag ? 1 : 0,
                float number => (double)number,
                _ => value,
            };
            command.Parameters.AddWithValue(name, bound);
        }
    }

    /// <summary>The version-1 schema. The views are the documented reading surface.</summary>
    private const string Schema = """
        CREATE TABLE games(
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE,
            title TEXT,
            directory TEXT NOT NULL,
            imported_utc TEXT NOT NULL,
            textures INTEGER NOT NULL DEFAULT 0,
            materials INTEGER NOT NULL DEFAULT 0,
            models INTEGER NOT NULL DEFAULT 0,
            issues INTEGER NOT NULL DEFAULT 0);

        CREATE TABLE textures(
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            width INTEGER NOT NULL,
            height INTEGER NOT NULL,
            format TEXT NOT NULL,
            mips INTEGER NOT NULL,
            frames INTEGER NOT NULL,
            flags TEXT NOT NULL,
            reflectivity_r REAL NOT NULL,
            reflectivity_g REAL NOT NULL,
            reflectivity_b REAL NOT NULL,
            average_colour TEXT NOT NULL,
            has_alpha INTEGER NOT NULL,
            image TEXT NOT NULL,
            image_width INTEGER NOT NULL,
            image_height INTEGER NOT NULL,
            UNIQUE(path, sha256));
        CREATE INDEX textures_path ON textures(path);

        CREATE TABLE game_textures(
            game_id INTEGER NOT NULL,
            texture_id INTEGER NOT NULL,
            source TEXT NOT NULL,
            PRIMARY KEY(game_id, texture_id)) WITHOUT ROWID;
        CREATE INDEX game_textures_texture ON game_textures(texture_id);

        CREATE TABLE materials(
            id INTEGER PRIMARY KEY,
            game_id INTEGER NOT NULL,
            path TEXT NOT NULL,
            shader TEXT NOT NULL,
            usage TEXT NOT NULL,
            category TEXT NOT NULL,
            base_texture TEXT,
            base_texture2 TEXT,
            bump_map TEXT,
            detail TEXT,
            env_map TEXT,
            tool_texture TEXT,
            surface_prop TEXT,
            surface_prop2 TEXT,
            keywords TEXT,
            translucent INTEGER NOT NULL,
            alpha_test INTEGER NOT NULL,
            self_illum INTEGER NOT NULL,
            include TEXT,
            parameters TEXT NOT NULL,
            vmt TEXT NOT NULL,
            source TEXT NOT NULL,
            texture_id INTEGER,
            UNIQUE(game_id, path));
        CREATE INDEX materials_path ON materials(path);
        CREATE INDEX materials_usage ON materials(game_id, usage);

        CREATE TABLE models(
            id INTEGER PRIMARY KEY,
            path TEXT NOT NULL,
            sha256 TEXT NOT NULL,
            name TEXT NOT NULL,
            flags INTEGER NOT NULL,
            static_prop INTEGER NOT NULL,
            surface_prop TEXT NOT NULL,
            mass REAL NOT NULL,
            size_x REAL NOT NULL, size_y REAL NOT NULL, size_z REAL NOT NULL,
            mesh_min_x REAL NOT NULL, mesh_min_y REAL NOT NULL, mesh_min_z REAL NOT NULL,
            mesh_max_x REAL NOT NULL, mesh_max_y REAL NOT NULL, mesh_max_z REAL NOT NULL,
            hull_min_x REAL NOT NULL, hull_min_y REAL NOT NULL, hull_min_z REAL NOT NULL,
            hull_max_x REAL NOT NULL, hull_max_y REAL NOT NULL, hull_max_z REAL NOT NULL,
            view_min_x REAL NOT NULL, view_min_y REAL NOT NULL, view_min_z REAL NOT NULL,
            view_max_x REAL NOT NULL, view_max_y REAL NOT NULL, view_max_z REAL NOT NULL,
            bones INTEGER NOT NULL,
            sequences INTEGER NOT NULL,
            body_parts INTEGER NOT NULL,
            lods INTEGER NOT NULL,
            triangles INTEGER NOT NULL,
            vertices INTEGER NOT NULL,
            skins INTEGER NOT NULL,
            has_collision INTEGER NOT NULL,
            prop_data TEXT,
            keyvalues TEXT NOT NULL,
            grid_step INTEGER NOT NULL,
            image_dir TEXT NOT NULL,
            image_front TEXT NOT NULL,
            image_back TEXT NOT NULL,
            image_left TEXT NOT NULL,
            image_right TEXT NOT NULL,
            image_top TEXT NOT NULL,
            image_iso TEXT NOT NULL,
            image_sheet TEXT NOT NULL,
            UNIQUE(path, sha256));
        CREATE INDEX models_path ON models(path);

        CREATE TABLE game_models(
            game_id INTEGER NOT NULL,
            model_id INTEGER NOT NULL,
            source TEXT NOT NULL,
            PRIMARY KEY(game_id, model_id)) WITHOUT ROWID;
        CREATE INDEX game_models_model ON game_models(model_id);

        CREATE TABLE model_materials(
            game_id INTEGER NOT NULL,
            model_id INTEGER NOT NULL,
            skin INTEGER NOT NULL,
            slot INTEGER NOT NULL,
            texture TEXT NOT NULL,
            material TEXT,
            PRIMARY KEY(game_id, model_id, skin, slot)) WITHOUT ROWID;

        CREATE TABLE issues(
            game_id INTEGER NOT NULL,
            kind TEXT NOT NULL,
            path TEXT NOT NULL,
            reason TEXT NOT NULL);
        CREATE INDEX issues_game ON issues(game_id, kind);

        CREATE VIEW material_view AS
            SELECT g.name AS game, m.path AS material, m.usage, m.category, m.shader,
                   m.surface_prop, m.keywords, m.translucent, m.alpha_test,
                   t.width, t.height, t.average_colour, t.has_alpha, t.image,
                   m.base_texture, m.base_texture2, m.bump_map, m.tool_texture, m.include
            FROM materials m
            JOIN games g ON g.id = m.game_id
            LEFT JOIN textures t ON t.id = m.texture_id;

        CREATE VIEW model_view AS
            SELECT g.name AS game, mo.path AS model, mo.static_prop, mo.prop_data, mo.surface_prop,
                   mo.size_x, mo.size_y, mo.size_z,
                   mo.mesh_min_x, mo.mesh_min_y, mo.mesh_min_z, mo.mesh_max_x, mo.mesh_max_y, mo.mesh_max_z,
                   mo.triangles, mo.skins, mo.sequences, mo.has_collision, mo.mass, mo.grid_step,
                   mo.image_sheet, mo.image_front, mo.image_back, mo.image_left, mo.image_right,
                   mo.image_top, mo.image_iso
            FROM game_models gm
            JOIN games g ON g.id = gm.game_id
            JOIN models mo ON mo.id = gm.model_id;

        CREATE VIEW texture_view AS
            SELECT g.name AS game, t.path AS texture, t.width, t.height, t.format, t.flags,
                   t.average_colour, t.has_alpha, t.image
            FROM game_textures gt
            JOIN games g ON g.id = gt.game_id
            JOIN textures t ON t.id = gt.texture_id;
        """;
}
