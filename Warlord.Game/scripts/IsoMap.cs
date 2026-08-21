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
    private SubViewportContainer _characterViewport = null!;
    private SettingsView _settingsView = null!;

    // Settlement style preview gallery (see PlaceSettlementPreviews) — world
    // -space bounds + label per marker, checked on mouse move so hovering one
    // shows its style name in the HUD.
    private readonly System.Collections.Generic.List<(Rect2 Bounds, string Label)> _settlementPreviews = new();
    private string? _hoveredSettlementLabel;

    public override async void _Ready()
    {
        KeyBindings.Initialize();

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
        BuildLineMesh(borders, new Color(0.12f, 0.10f, 0.09f, 0.7f), zIndex: 1); // smoothed province borders, drawn over mountains
        BuildLineMesh(coasts, new Color(0.05f, 0.09f, 0.15f, 0.9f));  // shoreline stroke (z -1)

        FrameCamera();

        // Mountain ranges are real 3D renders baked to textures, one per
        // contiguous cluster of mountain cells (see MountainBaker), so a
        // whole range reads as one connected chain instead of one blob per
        // cell. Baking is several GPU frames per cluster (more with shader
        // compile stalls on first use), so MaybeScreenshot's timer starts only
        // once this is done — otherwise a screenshot/headless run can quit
        // before a single sprite is placed.
        var clusters = MapGeometry.ExtractMountainClusters(_map);
        var bakes = await MountainBaker.BakeClustersAsync(this, clusters, seed: MapSeed);
        PlaceMountainSprites(bakes);

        // Settlement style preview gallery: one of each style, placed at
        // real province centroids (so they sit grounded on actual land
        // instead of floating as a HUD overlay), with mouseover names so
        // styles can be compared and picked before wiring any into real
        // per-province placement.
        var settlementBakes = await CityBaker.BakeAllStylesAsync(this);
        PlaceSettlementPreviews(settlementBakes);

        // Open focused on the comparison gallery rather than the whole map,
        // since right now picking a settlement style is the point — remove
        // this once a style is chosen and settlements move to real
        // per-province placement.
        if (_settlementPreviews.Count > 0)
        {
            Vector2 first = _settlementPreviews[0].Bounds.GetCenter();
            Vector2 last = _settlementPreviews[^1].Bounds.GetCenter();
            _camera.Position = (first + last) / 2f;
            _camera.Zoom = Vector2.One * 1.3f;
        }

        BuildModeMenu();
        BuildCharacterView();
        BuildSettingsView();

        MaybeScreenshot();
    }

    private enum GameMode { Map, Character, Settings }

    // A simple top menu: "Map" shows the existing 2D campaign map (unchanged
    // below), "Character" shows a live 3D preview instead, "Settings" shows
    // the key-binding panel. Both the character viewport and the settings
    // panel are screen-covering overlays under the same HUD CanvasLayer, so
    // they draw on top of the map without needing to touch any of the map's
    // own nodes — showing/hiding the right one is the entire mode switch.
    private void BuildModeMenu()
    {
        var hud = GetNode("HUD");

        var mapButton = new Button { Text = "Map", Position = new Vector2(500, 8), Size = new Vector2(90, 32) };
        mapButton.Pressed += () => ShowMode(GameMode.Map);
        hud.AddChild(mapButton);

        var characterButton = new Button { Text = "Character", Position = new Vector2(596, 8), Size = new Vector2(90, 32) };
        characterButton.Pressed += () => ShowMode(GameMode.Character);
        hud.AddChild(characterButton);

        var settingsButton = new Button { Text = "Settings", Position = new Vector2(692, 8), Size = new Vector2(90, 32) };
        settingsButton.Pressed += () => ShowMode(GameMode.Settings);
        hud.AddChild(settingsButton);
    }

    private void ShowMode(GameMode mode)
    {
        _characterViewport.Visible = mode == GameMode.Character;
        _settingsView.Visible = mode == GameMode.Settings;
    }

    private void BuildSettingsView()
    {
        _settingsView = new SettingsView { Visible = false };
        GetNode("HUD").AddChild(_settingsView);
    }

    // A live (not baked) 3D scene showing one warrior model — CC0 "Knight
    // Character" by Quaternius — slowly rotating (see CharacterView). Picked
    // as the starting model since it's a single self-contained FBX (no
    // external textures to wire up), unlike the multi-file RPG Character Pack.
    private void BuildCharacterView()
    {
        _characterViewport = new SubViewportContainer { Stretch = true, Visible = false };
        _characterViewport.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        GetNode("HUD").AddChild(_characterViewport); // attach before building children — LookAt below needs a live tree

        var viewport = new SubViewport
        {
            Size = new Vector2I(1280, 720),
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        _characterViewport.AddChild(viewport);

        viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.14f, 0.15f, 0.19f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.5f, 0.5f, 0.55f),
                AmbientLightEnergy = 0.8f,
            },
        });

        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-45f, -30f, 0f),
            LightEnergy = 1.2f,
        });

        // Flat ground, tiled with the same grass texture the 2D map uses for
        // Plains, so walking forward/back reads as movement across a real
        // surface rather than the character drifting through a void.
        viewport.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(200f, 200f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoTexture = GD.Load<Texture2D>("res://textures/terrain/plains.jpg"),
                Uv1Scale = new Vector3(40f, 40f, 1f),
            },
        });

        var rotator = new CharacterView();
        viewport.AddChild(rotator);

        // Camera is a child of CameraPitchPivot, itself a rigid child of
        // CameraRig (see CharacterView) — a separate pivot chain from
        // ModelPivot, so orbiting the camera (arrow keys) never turns the
        // model. Orbiting either pivot carries the camera around the
        // character while it keeps looking at CameraPitchPivot's origin,
        // since a rigid rotation of a parent preserves a child's relative
        // facing. No per-frame LookAt needed: at identity rotation, a node
        // at local +Z already faces -Z (Godot's forward), i.e. back toward
        // the pivot.
        var camera = new Camera3D { Position = new Vector3(0f, 0f, 6f) };
        rotator.CameraPitchPivot.AddChild(camera);

        var knightScene = GD.Load<PackedScene>("res://models/characters/KnightCharacter.fbx");
        var knight = knightScene.Instantiate<Node3D>();
        rotator.ModelPivot.AddChild(knight);

        // The model's own authored Idle/Walking animations (see CharacterView)
        // rather than hand-posing bones — a few attempts at manually posing
        // the elbow (guessing which local axis is the hinge) all either
        // clipped the forearm through the torso or swung it in some unnatural
        // direction, since the rig's actual rest-pose axis conventions aren't
        // easy to reason out from GetBoneRest() alone.
        var animPlayer = FindAnimationPlayer(knight);
        if (animPlayer is not null) rotator.SetAnimationPlayer(animPlayer);
    }

    private static AnimationPlayer? FindAnimationPlayer(Node node)
    {
        if (node is AnimationPlayer player) return player;
        foreach (Node child in node.GetChildren())
        {
            var found = FindAnimationPlayer(child);
            if (found is not null) return found;
        }
        return null;
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

    // Rough per-terrain elevation, 0..1. Piggybacks on the terrain texture's
    // otherwise-unused alpha channel so the shader can fake hillshading from
    // the height gradient without a second texture. First-pass numbers, not
    // tuned to anything — nudge freely once we see how it reads in-game.
    private static float TerrainHeight(Terrain t) => t switch
    {
        Terrain.Water => 0f,
        Terrain.Coast => 0.05f,
        Terrain.Plains => 0.15f,
        Terrain.Forest => 0.25f,
        Terrain.Hills => 0.5f,
        Terrain.Mountains => 0.9f,
        _ => 0f,
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
                    if (t != Terrain.Water)
                    {
                        Color color = TerrainColor(t);
                        color.A = TerrainHeight(t);
                        img.SetPixel(c, r, color);
                        filled[c, r] = true;
                    }
                    else
                    {
                        Color sea = SeaColor;
                        sea.A = 0f;
                        img.SetPixel(c, r, sea);
                    }
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
        material.SetShaderParameter("coast_tex", GD.Load<Texture2D>("res://textures/terrain/coast.jpg"));
        material.SetShaderParameter("plains_tex", GD.Load<Texture2D>("res://textures/terrain/plains.jpg"));
        material.SetShaderParameter("forest_tex", GD.Load<Texture2D>("res://textures/terrain/forest.jpg"));
        material.SetShaderParameter("hills_tex", GD.Load<Texture2D>("res://textures/terrain/hills.jpg"));
        material.SetShaderParameter("mountains_tex", GD.Load<Texture2D>("res://textures/terrain/mountains.jpg"));

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
        Color color, int zIndex = -1)
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

        AddChild(new MeshInstance2D { Mesh = st.Commit(), ZIndex = zIndex });
    }

    private static void AddVertex(SurfaceTool st, Vector2 pos, Color color)
    {
        st.SetColor(color);
        st.SetUV(pos);
        st.AddVertex(new Vector3(pos.X, pos.Y, 0));
    }

    // One sprite per mountain CLUSTER (not per cell) — its baked texture
    // already covers the cluster's whole footprint as one connected shape.
    // Screen size/position reuse CellToScreen's own formula (span * half-tile)
    // so the sprite's on-screen bounding box lines up with where those cells
    // actually project isometrically. Nudged up so the cluster's base (not
    // its texture-space center) lands on the ground rather than floating.
    private void PlaceMountainSprites(System.Collections.Generic.List<MountainBake> bakes)
    {
        foreach (var bake in bakes)
        {
            float spanCells = bake.ColSpan + bake.RowSpan;
            float screenWidth = spanCells * (TileWidth / 2f);
            float screenHeight = spanCells * (TileHeight / 2f);
            Vector2 scale = new Vector2(screenWidth, screenHeight) / (Vector2)bake.Texture.GetSize();

            Vector2 center = new Vector2(
                (bake.CenterCol - bake.CenterRow) * (TileWidth / 2f),
                (bake.CenterCol + bake.CenterRow) * (TileHeight / 2f));

            AddChild(new Sprite2D
            {
                Texture = bake.Texture,
                Position = center + new Vector2(0, -screenHeight * 0.28f),
                Scale = scale,
                ZIndex = 0,
                TextureFilter = TextureFilterEnum.Linear,
            });
        }
    }

    // A comparison set of one marker per settlement style, placed in the
    // WORLD (children of this map, not the HUD) so each sits grounded on
    // actual land and pans/zooms with the map like every other map feature.
    // All of them are clustered in a grid at one anchor point (the largest
    // province, for open ground) rather than scattered one-per-province —
    // scattered across far-apart provinces at the same tiny on-screen size,
    // there was no way to tell them apart without zooming into each in turn.
    // World-space bounds + style name are stashed for the mouse-move hover
    // check in _Input, and a small always-visible label sits under each one
    // so identifying a style doesn't require hovering at all.
    // Converts a settlement's real world-unit footprint (bake.FootprintX +
    // FootprintZ) into map "span cells" using the same convention as the
    // rest of the map: a span of 2 exactly covers one grid cell's diamond
    // (see FrameCamera's (Columns+Rows) formula). Calibrated so City (the
    // requested "one tile plus a bit") lands close to a span of 2.2.
    private const float WorldUnitsPerCell = 4.7f;

    private void PlaceSettlementPreviews(System.Collections.Generic.List<SettlementBake> bakes)
    {
        _settlementPreviews.Clear();

        Province anchor = _map.Provinces[0];
        foreach (var province in _map.Provinces)
            if (province.Cells.Count > anchor.Cells.Count) anchor = province;

        float anchorCol = 0f, anchorRow = 0f;
        foreach (var cell in anchor.Cells)
        {
            anchorCol += cell.Col;
            anchorRow += cell.Row;
        }
        anchorCol /= anchor.Cells.Count;
        anchorRow /= anchor.Cells.Count;

        // Snapped to whole cells: CellToScreen(col, row) for integer col/row
        // round-trips exactly back through ScreenToCell to that same cell, so
        // hovering a marker highlights the tile it's actually centered on.
        // Fractional placement (an averaged anchor plus non-integer spacing)
        // put the marker's center and its "attached" tile a cell or more apart.
        int anchorColInt = Mathf.RoundToInt(anchorCol);
        int anchorRowInt = Mathf.RoundToInt(anchorRow);

        const int columns = 4;
        const int gridSpacingCells = 4;

        for (int i = 0; i < bakes.Count; i++)
        {
            var bake = bakes[i];
            int col = anchorColInt + (i % columns) * gridSpacingCells;
            int row = anchorRowInt + (i / columns) * gridSpacingCells;

            float spanCells = (bake.FootprintX + bake.FootprintZ) / WorldUnitsPerCell;
            float screenWidth = spanCells * (TileWidth / 2f);
            float screenHeight = spanCells * (TileHeight / 2f);

            Vector2 center = new Vector2((col - row) * (TileWidth / 2f), (col + row) * (TileHeight / 2f));
            Vector2 scale = new Vector2(screenWidth, screenHeight) / (Vector2)bake.Texture.GetSize();
            Vector2 pos = center; // sprite is centered (Sprite2D default), so this centers it on the tile

            AddChild(new Sprite2D
            {
                Texture = bake.Texture,
                Position = pos,
                Scale = scale,
                ZIndex = 2,
                TextureFilter = TextureFilterEnum.Linear,
            });

            AddChild(new Label
            {
                Text = bake.Name,
                Position = center + new Vector2(-60, 6),
                Size = new Vector2(120, 20),
                HorizontalAlignment = HorizontalAlignment.Center,
                ZIndex = 3,
            });

            var bounds = new Rect2(
                pos - new Vector2(screenWidth, screenHeight) * 0.5f, new Vector2(screenWidth, screenHeight));
            _settlementPreviews.Add((bounds, bake.Name));
        }
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

            string? hoveredSettlement = null;
            Vector2 worldMouse = GetGlobalMousePosition();
            foreach (var (bounds, label) in _settlementPreviews)
            {
                if (bounds.HasPoint(worldMouse))
                {
                    hoveredSettlement = label;
                    break;
                }
            }
            if (hoveredSettlement != _hoveredSettlementLabel)
            {
                _hoveredSettlementLabel = hoveredSettlement;
                if (hoveredSettlement is not null) _info.Text = $"Settlement preview: {hoveredSettlement}";
                else UpdateInfoLabel();
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
                    ZoomAt(1.1f);
                    break;
                case MouseButton.WheelDown when mb.Pressed:
                    ZoomAt(0.9f);
                    break;
            }
        }
    }

    // Zoom while keeping the world point under the mouse cursor fixed on screen,
    // by measuring how the cursor's world position shifts once the zoom is
    // applied and pulling the camera back by that offset.
    private void ZoomAt(float factor)
    {
        Vector2 before = GetGlobalMousePosition();
        _camera.Zoom *= factor;
        Vector2 after = GetGlobalMousePosition();
        _camera.Position += before - after;
    }

    public override void _Process(double delta)
    {
        // Arrow keys/WASD drive the character or the settings rebind capture
        // instead — don't also pan the (hidden) 2D map camera, or it
        // silently drifts while you're in another mode and lands somewhere
        // odd when you switch back. Null-checked: _Process runs every frame
        // independent of _Ready's async bake chain, so it can fire before
        // BuildCharacterView/BuildSettingsView have run.
        if (_characterViewport?.Visible == true || _settingsView?.Visible == true) return;

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
