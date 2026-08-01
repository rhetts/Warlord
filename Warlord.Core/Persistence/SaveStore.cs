using Microsoft.Data.Sqlite;
using Warlord.Core.State;

namespace Warlord.Core.Persistence;

/// <summary>
/// Reads and writes campaign state to a SQLite save file. This is the single
/// boundary between game logic and storage: the rest of the game deals in
/// CampaignMapState objects and never sees SQL. Each save fully rewrites the
/// slot inside one transaction, so a crash mid-save can't leave a torn file.
/// </summary>
public sealed class SaveStore
{
    /// <summary>
    /// Bump when the on-disk shape changes. Load rejects unknown versions so
    /// old saves fail loudly (and can later be migrated) rather than silently
    /// loading garbage.
    /// </summary>
    public const int SchemaVersion = 2;

    private readonly string _path;

    public SaveStore(string dbFilePath) => _path = dbFilePath;

    public bool Exists() => File.Exists(_path);

    private SqliteConnection Open()
    {
        // Pooling=False so the file handle is released as soon as the
        // connection is disposed — otherwise a pooled handle keeps the save
        // file locked, blocking deletes/copies of the slot.
        var conn = new SqliteConnection($"Data Source={_path};Pooling=False");
        conn.Open();
        return conn;
    }

    public void Save(CampaignMapState map)
    {
        using var conn = Open();
        using var tx = conn.BeginTransaction();

        Exec(conn, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS factions (id INTEGER PRIMARY KEY, name TEXT NOT NULL, r INTEGER, g INTEGER, b INTEGER);
            CREATE TABLE IF NOT EXISTS provinces (id INTEGER PRIMARY KEY, name TEXT NOT NULL, owner_faction_id INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS province_cells (province_id INTEGER NOT NULL, col INTEGER NOT NULL, row INTEGER NOT NULL, terrain INTEGER NOT NULL DEFAULT 0);
            DELETE FROM meta;
            DELETE FROM factions;
            DELETE FROM provinces;
            DELETE FROM province_cells;
            """);

        SetMeta(conn, "schema_version", SchemaVersion.ToString());
        SetMeta(conn, "columns", map.Columns.ToString());
        SetMeta(conn, "rows", map.Rows.ToString());

        foreach (var f in map.Factions)
            Exec(conn,
                "INSERT INTO factions (id, name, r, g, b) VALUES (:id, :name, :r, :g, :b);",
                ("id", f.Id), ("name", f.Name), ("r", f.Color.R), ("g", f.Color.G), ("b", f.Color.B));

        foreach (var p in map.Provinces)
        {
            Exec(conn,
                "INSERT INTO provinces (id, name, owner_faction_id) VALUES (:id, :name, :owner);",
                ("id", p.Id), ("name", p.Name), ("owner", p.OwnerFactionId));

            foreach (var cell in p.Cells)
                Exec(conn,
                    "INSERT INTO province_cells (province_id, col, row, terrain) VALUES (:pid, :col, :row, :terrain);",
                    ("pid", p.Id), ("col", cell.Col), ("row", cell.Row), ("terrain", (int)map.TerrainAt(cell)));
        }

        tx.Commit();
    }

    public CampaignMapState Load()
    {
        using var conn = Open();

        int version = int.Parse(GetMeta(conn, "schema_version"));
        if (version != SchemaVersion)
            throw new InvalidOperationException(
                $"Save file schema version {version} is not supported (expected {SchemaVersion}).");

        int columns = int.Parse(GetMeta(conn, "columns"));
        int rows = int.Parse(GetMeta(conn, "rows"));

        var factions = new List<Faction>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, r, g, b FROM factions ORDER BY id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                factions.Add(new Faction(
                    r.GetInt32(0), r.GetString(1),
                    new RgbColor((byte)r.GetInt32(2), (byte)r.GetInt32(3), (byte)r.GetInt32(4))));
        }

        // province id -> its cells, plus a cell -> terrain map
        var cellsByProvince = new Dictionary<int, List<Cell>>();
        var terrain = new Dictionary<Cell, Terrain>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT province_id, col, row, terrain FROM province_cells;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int pid = r.GetInt32(0);
                var cell = new Cell(r.GetInt32(1), r.GetInt32(2));
                if (!cellsByProvince.TryGetValue(pid, out var list))
                    cellsByProvince[pid] = list = new List<Cell>();
                list.Add(cell);
                terrain[cell] = (Terrain)r.GetInt32(3);
            }
        }

        var provinces = new List<Province>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name, owner_faction_id FROM provinces ORDER BY id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int id = r.GetInt32(0);
                provinces.Add(new Province
                {
                    Id = id,
                    Name = r.GetString(1),
                    OwnerFactionId = r.GetInt32(2),
                    Cells = cellsByProvince.GetValueOrDefault(id, new List<Cell>()),
                });
            }
        }

        return new CampaignMapState(columns, rows, factions, provinces, terrain);
    }

    private static void SetMeta(SqliteConnection conn, string key, string value) =>
        Exec(conn, "INSERT INTO meta (key, value) VALUES (:k, :v);", ("k", key), ("v", value));

    private static string GetMeta(SqliteConnection conn, string key)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = :k;";
        cmd.Parameters.AddWithValue(":k", key);
        return cmd.ExecuteScalar() as string
            ?? throw new InvalidOperationException($"Save file is missing meta key '{key}'.");
    }

    private static void Exec(SqliteConnection conn, string sql, params (string name, object value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(":" + name, value);
        cmd.ExecuteNonQuery();
    }
}
