using Godot;
using Warlord.Core.Generation;
using Warlord.Core.Persistence;
using Warlord.Core.State;

namespace Warlord.Game;

/// <summary>
/// Renders the campaign map isometrically from a Warlord.Core CampaignMapState.
/// Tiles are colored by the owning faction; thicker borders are drawn between
/// provinces. This class only *draws* Core state — it holds no game logic.
///
/// Pan: right/middle-drag or arrows/WASD. Zoom: mouse wheel.
/// </summary>
public partial class IsoMap : Node2D
{
    // Tile diamond dimensions in pixels (classic 2:1 isometric ratio).
    private const int TileWidth = 128;
    private const int TileHeight = 64;

    // Fixed seed for now so the generated map is reproducible; a "new campaign"
    // flow would randomize this.
    private const int MapSeed = 12345;

    private Camera2D _camera = null!;
    private Label _info = null!;
    private CampaignMapState _map = null!;
    private Vector2I _hoveredCell = new(-1, -1);
    private bool _dragging;

    public override void _Ready()
    {
        _camera = GetNode<Camera2D>("Camera2D");
        _info = GetNode<Label>("HUD/Info");
        _map = LoadOrSeedMap();
        FrameCamera();
    }

    /// <summary>Center the camera on the map and zoom so the whole thing fits.</summary>
    private void FrameCamera()
    {
        _camera.Position = CellToScreen((_map.Columns - 1) / 2, (_map.Rows - 1) / 2);

        float mapWidth = (_map.Columns + _map.Rows) * (TileWidth / 2f);
        float mapHeight = (_map.Columns + _map.Rows) * (TileHeight / 2f);
        Vector2 viewport = GetViewportRect().Size;
        float zoom = Mathf.Min(viewport.X / mapWidth, viewport.Y / mapHeight) * 0.9f;
        _camera.Zoom = new Vector2(zoom, zoom);
    }

    /// <summary>
    /// Loads the campaign from the SQLite save file, seeding it from the demo
    /// map on first run. The save lives under Godot's user data dir; we hand
    /// SaveStore a real filesystem path via GlobalizePath.
    /// </summary>
    private static CampaignMapState LoadOrSeedMap()
    {
        string dbPath = ProjectSettings.GlobalizePath("user://warlord.db");
        var store = new SaveStore(dbPath);

        if (!store.Exists())
        {
            GD.Print($"No save found — generating new campaign at {dbPath}");
            var generated = MapGenerator.Generate(
                seed: MapSeed, columns: 100, rows: 100, provinceCount: 160);
            store.Save(generated);
            return generated;
        }

        GD.Print($"Loading campaign from {dbPath}");
        return store.Load();
    }

    /// <summary>Logical grid cell -> screen-space center of its diamond.</summary>
    private static Vector2 CellToScreen(int col, int row)
    {
        float x = (col - row) * (TileWidth / 2f);
        float y = (col + row) * (TileHeight / 2f);
        return new Vector2(x, y);
    }

    /// <summary>Screen-space point -> logical grid cell (inverse projection).</summary>
    private static Vector2I ScreenToCell(Vector2 p)
    {
        float fx = p.X / (TileWidth / 2f);
        float fy = p.Y / (TileHeight / 2f);
        int col = Mathf.FloorToInt((fx + fy) / 2f);
        int row = Mathf.FloorToInt((fy - fx) / 2f);
        return new Vector2I(col, row);
    }

    private static Color ToColor(RgbColor c) =>
        new(c.R / 255f, c.G / 255f, c.B / 255f);

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion)
        {
            var cell = ScreenToCell(GetGlobalMousePosition());
            if (cell != _hoveredCell)
            {
                _hoveredCell = cell;
                UpdateInfoLabel();
                QueueRedraw();
            }

            if (_dragging)
                _camera.Position -= motion.Relative / _camera.Zoom;
        }
        else if (@event is InputEventMouseButton mb)
        {
            switch (mb.ButtonIndex)
            {
                case MouseButton.Middle:
                case MouseButton.Right:
                    _dragging = mb.Pressed;
                    break;
                case MouseButton.WheelUp when mb.Pressed:
                    _camera.Zoom *= 1.1f;
                    break;
                case MouseButton.WheelDown when mb.Pressed:
                    _camera.Zoom *= 0.9f;
                    break;
            }
        }
    }

    public override void _Process(double delta)
    {
        var dir = Input.GetVector("ui_left", "ui_right", "ui_up", "ui_down");
        if (dir != Vector2.Zero)
            _camera.Position += dir * (float)delta * 400f / _camera.Zoom.X;
    }

    private void UpdateInfoLabel()
    {
        var cell = new Cell(_hoveredCell.X, _hoveredCell.Y);
        var province = _map.ProvinceAt(cell);
        if (province is null)
        {
            _info.Text = "—";
            return;
        }

        var owner = _map.OwnerOf(province);
        _info.Text = $"{province.Name}  ·  {owner?.Name ?? "unclaimed"}";
    }

    public override void _Draw()
    {
        for (int row = 0; row < _map.Rows; row++)
            for (int col = 0; col < _map.Columns; col++)
                DrawTile(col, row);
    }

    private static readonly Color SeaColor = new("2a5a7a");

    private void DrawTile(int col, int row)
    {
        var cell = new Cell(col, row);
        Vector2 c = CellToScreen(col, row);
        Vector2 top = c + new Vector2(0, -TileHeight / 2f);
        Vector2 right = c + new Vector2(TileWidth / 2f, 0);
        Vector2 bottom = c + new Vector2(0, TileHeight / 2f);
        Vector2 left = c + new Vector2(-TileWidth / 2f, 0);

        var province = _map.ProvinceAt(cell);

        // Water: cells not in any province render as sea, no borders.
        if (province is null)
        {
            DrawColoredPolygon(new[] { top, right, bottom, left }, SeaColor);
            return;
        }

        // Fill by owning faction; lighten the tile under the cursor.
        var owner = _map.OwnerOf(province);
        Color fill = owner is null ? new Color("555555") : ToColor(owner.Color);
        bool hovered = col == _hoveredCell.X && row == _hoveredCell.Y;
        if (hovered)
            fill = fill.Lightened(0.35f);

        DrawColoredPolygon(new[] { top, right, bottom, left }, fill);

        // Light per-tile grid line.
        DrawPolyline(new[] { top, right, bottom, left, top }, new Color(0, 0, 0, 0.15f), 1f);

        // Thick province border on any edge whose neighbor is a different province.
        DrawProvinceBorder(province, top, right, new Cell(col, row - 1));   // up-right edge
        DrawProvinceBorder(province, right, bottom, new Cell(col + 1, row)); // down-right edge
        DrawProvinceBorder(province, bottom, left, new Cell(col, row + 1));  // down-left edge
        DrawProvinceBorder(province, left, top, new Cell(col - 1, row));     // up-left edge
    }

    private void DrawProvinceBorder(Province self, Vector2 a, Vector2 b, Cell neighbor)
    {
        if (_map.ProvinceAt(neighbor) != self)
            DrawLine(a, b, new Color(0, 0, 0, 0.9f), 3f);
    }
}
