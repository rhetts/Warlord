using System;
using Godot;
using Warlord.Core.Generation;
using Warlord.Core.Geometry;
using Warlord.Core.Persistence;
using Warlord.Core.State;

namespace Warlord.Game;

/// <summary>
/// Renders the campaign map isometrically from a Warlord.Core CampaignMapState.
///
/// Land is drawn as the smoothed coastline polygon (rounded shape), filled by a
/// shader that samples per-cell terrain and faction lookup textures — so the
/// coast is a genuine smooth curve, not the blocky per-cell grid. Inland lakes
/// are rounded sea polygons; province borders and the coastline are line meshes.
/// Only the cursor highlight redraws each frame. This class only *draws* Core
/// state — it holds no game logic.
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

    private static readonly Color SeaColor = new("21506e");

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

        RenderingServer.SetDefaultClearColor(SeaColor);

        var coasts = MapGeometry.ExtractCoastlines(_map, smoothingIterations: 3, roughness: 0.5f);
        var terrainTex = BuildCellTexture(terrainOnly: true);
        var factionTex = BuildCellTexture(terrainOnly: false);

        var borders = MapGeometry.ExtractProvinceOutlines(_map, smoothingIterations: 3, roughness: 0.5f);

        BuildLandFill(coasts, terrainTex, factionTex);  // rounded, texture-filled land (z -3)
        BuildLakeFill(coasts);                          // rounded inland lakes (z -2)
        BuildLineMesh(borders, new Color(0.12f, 0.10f, 0.09f, 0.7f)); // smoothed province borders (z -1)
        BuildLineMesh(coasts, new Color(0.05f, 0.09f, 0.15f, 0.9f));  // shoreline stroke (z -1)

        FrameCamera();
        MaybeScreenshot();
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

    // Save a screenshot and quit — used with `-- --shot` for headless verification.
    private void MaybeScreenshot()
    {
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (arg != "--shot") continue;
            GetTree().CreateTimer(1.0).Timeout += () =>
            {
                var image = GetViewport().GetTexture().GetImage();
                image.SavePng("user://shot.png");
                GetTree().Quit();
            };
            return;
        }
    }

    /// <summary>
    /// Loads the campaign from the SQLite save file, generating a new one on
    /// first run.
    /// </summary>
    private static CampaignMapState LoadOrSeedMap()
    {
        string dbPath = ProjectSettings.GlobalizePath("user://warlord.db");
        var store = new SaveStore(dbPath);

        if (store.Exists())
        {
            try
            {
                GD.Print($"Loading campaign from {dbPath}");
                return store.Load();
            }
            catch (Exception e)
            {
                GD.PushWarning($"Could not load save ({e.Message}); regenerating.");
            }
        }

        GD.Print($"Generating new campaign at {dbPath}");
        var generated = MapGenerator.Generate(
            seed: MapSeed, columns: 100, rows: 100, provinceCount: 54);
        store.Save(generated);
        return generated;
    }

    // ---- Projection ------------------------------------------------------

    private static Vector2 CellToScreen(int col, int row)
    {
        float x = (col - row) * (TileWidth / 2f);
        float y = (col + row) * (TileHeight / 2f);
        return new Vector2(x, y);
    }

    /// <summary>Grid-corner point -> screen space (aligns with diamond vertices).</summary>
    private static Vector2 ProjectCorner(float gx, float gy) =>
        new((gx - gy) * (TileWidth / 2f), (gx + gy - 1f) * (TileHeight / 2f));

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

    private static Color TerrainColor(Terrain t) => t switch
    {
        Terrain.Water => SeaColor,
        Terrain.Coast => new Color("cbb682"),
        Terrain.Plains => new Color("7aa653"),
        Terrain.Forest => new Color("2f6b3a"),
        Terrain.Hills => new Color("9c7f4e"),
        Terrain.Mountains => new Color("8f8f8f"),
        _ => SeaColor,
    };

    private Terrain TerrainAt(int col, int row) => _map.TerrainAt(new Cell(col, row));

    // ---- Lookup textures -------------------------------------------------

    // A per-cell color texture sampled by the fill shader. terrainOnly=true builds
    // the terrain palette (opaque); false builds faction tint (translucent, 0 on
    // sea). Land colors are dilated a few cells into the sea so the rounded coast
    // — which can sit up to ~1 cell beyond the blocky land — never samples blue.
    private ImageTexture BuildCellTexture(bool terrainOnly)
    {
        int w = _map.Columns, h = _map.Rows;
        var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
        var filled = new bool[w, h];

        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                if (terrainOnly)
                {
                    var t = TerrainAt(c, r);
                    if (t != Terrain.Water) { img.SetPixel(c, r, TerrainColor(t)); filled[c, r] = true; }
                    else img.SetPixel(c, r, SeaColor);
                }
                else
                {
                    img.SetPixel(c, r, new Color(0, 0, 0, 0));
                }
            }
        }

        if (!terrainOnly)
        {
            foreach (var province in _map.Provinces)
            {
                var owner = _map.OwnerOf(province);
                if (owner is null) continue;
                Color tint = ToColor(owner.Color);
                tint.A = 0.30f;
                foreach (var cell in province.Cells)
                {
                    img.SetPixel(cell.Col, cell.Row, tint);
                    filled[cell.Col, cell.Row] = true;
                }
            }
        }

        DilateImage(img, filled, passes: 3);
        return ImageTexture.CreateFromImage(img);
    }

    // Spread filled colors one cell outward per pass, so coastal sampling stays
    // on land color rather than sea.
    private static void DilateImage(Image img, bool[,] filled, int passes)
    {
        int w = img.GetWidth(), h = img.GetHeight();
        for (int p = 0; p < passes; p++)
        {
            var additions = new System.Collections.Generic.List<(int c, int r, Color color)>();
            for (int r = 0; r < h; r++)
            {
                for (int c = 0; c < w; c++)
                {
                    if (filled[c, r]) continue;
                    if (c > 0 && filled[c - 1, r]) additions.Add((c, r, img.GetPixel(c - 1, r)));
                    else if (c < w - 1 && filled[c + 1, r]) additions.Add((c, r, img.GetPixel(c + 1, r)));
                    else if (r > 0 && filled[c, r - 1]) additions.Add((c, r, img.GetPixel(c, r - 1)));
                    else if (r < h - 1 && filled[c, r + 1]) additions.Add((c, r, img.GetPixel(c, r + 1)));
                }
            }
            foreach (var (c, r, color) in additions)
            {
                img.SetPixel(c, r, color);
                filled[c, r] = true;
            }
        }
    }

    // ---- Mesh building (once, in _Ready) ---------------------------------

    // Fill each outer island (the smoothed coastline polygon) with a triangle
    // mesh; the shader colors each pixel from the lookup textures by UV.
    private void BuildLandFill(
        System.Collections.Generic.List<System.Collections.Generic.List<System.Numerics.Vector2>> coasts,
        ImageTexture terrainTex, ImageTexture factionTex)
    {
        int outerSign = OuterSign(coasts);
        float cols = _map.Columns, rows = _map.Rows;

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        bool any = false;

        foreach (var loop in coasts)
        {
            if (Math.Sign(SignedArea(loop)) != outerSign) continue; // skip lakes

            var pts = new Vector2[loop.Count];
            for (int i = 0; i < loop.Count; i++)
                pts[i] = ProjectCorner(loop[i].X, loop[i].Y);

            int[] indices = Geometry2D.TriangulatePolygon(pts);
            if (indices.Length == 0) continue;

            foreach (int idx in indices)
            {
                var g = loop[idx];
                st.SetUV(new Vector2(g.X / cols, g.Y / rows));
                st.SetColor(Colors.White);
                st.AddVertex(new Vector3(pts[idx].X, pts[idx].Y, 0));
            }
            any = true;
        }

        if (!any) return;

        var shader = GD.Load<Shader>("res://shaders/terrain.gdshader");
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("terrain_tex", terrainTex);
        material.SetShaderParameter("faction_tex", factionTex);

        AddChild(new MeshInstance2D { Mesh = st.Commit(), Material = material, ZIndex = -3 });
    }

    // Inland lakes: coastline loops wound opposite the outer islands, filled with
    // sea color so they read like the ocean coast.
    private void BuildLakeFill(
        System.Collections.Generic.List<System.Collections.Generic.List<System.Numerics.Vector2>> coasts)
    {
        if (coasts.Count < 2) return;
        int outerSign = OuterSign(coasts);

        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        bool anyLake = false;

        foreach (var loop in coasts)
        {
            if (Math.Sign(SignedArea(loop)) == outerSign) continue; // outer island

            var pts = new Vector2[loop.Count];
            for (int i = 0; i < loop.Count; i++)
                pts[i] = ProjectCorner(loop[i].X, loop[i].Y);

            int[] indices = Geometry2D.TriangulatePolygon(pts);
            if (indices.Length == 0) continue;

            foreach (int idx in indices)
                AddVertex(st, pts[idx], SeaColor);
            anyLake = true;
        }

        if (anyLake)
            AddChild(new MeshInstance2D { Mesh = st.Commit(), ZIndex = -2 });
    }

    private static int OuterSign(
        System.Collections.Generic.List<System.Collections.Generic.List<System.Numerics.Vector2>> coasts)
    {
        int sign = 0;
        float maxArea = -1f;
        foreach (var loop in coasts)
        {
            float area = SignedArea(loop);
            if (Mathf.Abs(area) > maxArea) { maxArea = Mathf.Abs(area); sign = Math.Sign(area); }
        }
        return sign;
    }

    private static float SignedArea(System.Collections.Generic.List<System.Numerics.Vector2> loop)
    {
        double area = 0;
        int n = loop.Count;
        for (int i = 0; i < n; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % n];
            area += (double)p.X * q.Y - (double)q.X * p.Y;
        }
        return (float)(area * 0.5);
    }

    // Draw a set of smoothed loops (coast or province outlines) as a line mesh.
    private void BuildLineMesh(
        System.Collections.Generic.List<System.Collections.Generic.List<System.Numerics.Vector2>> loops,
        Color color)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Lines);

        foreach (var loop in loops)
        {
            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = ProjectCorner(loop[i].X, loop[i].Y);
                Vector2 b = ProjectCorner(loop[(i + 1) % n].X, loop[(i + 1) % n].Y);
                AddVertex(st, a, color);
                AddVertex(st, b, color);
            }
        }

        AddChild(new MeshInstance2D { Mesh = st.Commit(), ZIndex = -1 });
    }

    private static void AddVertex(SurfaceTool st, Vector2 pos, Color color)
    {
        st.SetColor(color);
        st.SetUV(pos);
        st.AddVertex(new Vector3(pos.X, pos.Y, 0));
    }

    // ---- Input & per-frame hover -----------------------------------------

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
        _info.Text = $"{province.Name}  ·  {owner?.Name ?? "unclaimed"}  ·  {_map.TerrainAt(cell)}";
    }

    public override void _Draw()
    {
        var cell = new Cell(_hoveredCell.X, _hoveredCell.Y);
        if (_map.ProvinceAt(cell) is null)
            return;

        Vector2 c = CellToScreen(_hoveredCell.X, _hoveredCell.Y);
        var diamond = new[]
        {
            c + new Vector2(0, -TileHeight / 2f),
            c + new Vector2(TileWidth / 2f, 0),
            c + new Vector2(0, TileHeight / 2f),
            c + new Vector2(-TileWidth / 2f, 0),
        };
        DrawColoredPolygon(diamond, new Color(1f, 0.9f, 0.4f, 0.4f));
    }
}
