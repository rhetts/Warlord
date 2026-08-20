using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Warlord.Game;

// FootprintX/Z are the assembled scene's real world-space extents (not a
// guessed display size) — IsoMap uses these to size the on-screen sprite,
// so every style shares the same world-units-to-screen-pixels ratio and a
// building is the same size everywhere, while a style with a genuinely
// bigger footprint (more houses spread further apart) reads as bigger.
public readonly record struct SettlementBake(string Name, ImageTexture Texture, float FootprintX, float FootprintZ);

/// <summary>
/// Renders a set of named settlement styles into an offscreen 3D viewport
/// and bakes each into a reusable 2D texture — same technique as
/// MountainBaker, so these markers share the mountains' lighting/style
/// instead of looking like pasted-in icons. Two families of pieces:
/// Kenney's CC0 Castle Kit (towers/walls/gates, in models/castle/) for
/// fortified styles, and Kenney's CC0 City Kit Suburban (houses/trees, in
/// models/town/) for settlement styles — so a "town" reads as an actual
/// cluster of distinct houses, not a scaled-up tower.
///
/// Camera framing (bake resolution + per-style Size/look-target) is a fixed
/// texture size plus hand-picked per-style values — separately, each
/// style's real world-space footprint is auto-measured (see Assembly) and
/// reported via FootprintX/Z so IsoMap can size the on-screen marker
/// consistently across styles (that was the earlier size-inconsistency
/// bug: City's houses baked smaller than Village's because more of them had
/// to fit the same frame). The two concerns are independent: this fixes
/// on-screen relative size without touching how each style's bake camera
/// is framed, which stays exactly as tuned by eye.
/// </summary>
public static class CityBaker
{
    private const int TextureSize = 512;

    // Camera framing (size + look-target) is hand-picked per style, same
    // values used before the footprint rewrite — restored exactly rather
    // than derived from a blanket formula, since the old values were never
    // a consistent formula to begin with (their size-to-target-height ratio
    // ranges from 4% to 33% across styles) and no single auto-formula
    // reproduces all seven. FootprintX/Z for on-screen map sizing still
    // comes from the auto-measured Assembly.Bounds below — that fix is
    // independent of camera framing and unaffected by this revert.
    private static readonly (string Name, Func<Assembly> Build, float CameraSize, Vector3 CameraCenter)[] Styles =
    {
        ("Village", BuildVillage, 4.5f, new Vector3(0.5f, 0.4f, 0.3f)),
        ("Town", BuildTown, 8f, new Vector3(0.8f, 0.5f, 0.8f)),
        ("City", BuildCity, 11f, new Vector3(1.9f, 0.5f, 0.95f)),
        ("Hexagon Watchtower", BuildHexagonKeep, 4.5f, new Vector3(0f, 1.3f, 0f)),
        ("High-Roof Keep", BuildHighRoofKeep, 4.5f, new Vector3(0f, 1.5f, 0f)),
        ("Walled Keep", BuildWalledKeep, 8f, new Vector3(1f, 1.3f, 0.5f)),
        ("Castle", BuildCastle, 13f, new Vector3(1.5f, 1.5f, 1.5f)),
    };

    public static async Task<List<SettlementBake>> BakeAllStylesAsync(Node host)
    {
        var viewport = new SubViewport
        {
            Size = new Vector2I(TextureSize, TextureSize),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
        };
        host.AddChild(viewport);

        viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.ClearColor,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.4f, 0.42f, 0.46f),
                AmbientLightEnergy = 0.7f,
            },
        });

        viewport.AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55f, -35f, 0f),
            LightEnergy = 1.2f,
        });

        var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal };
        // Steeper than MountainBaker's (4, 3.2, 4) ~30° elevation — that angle
        // shows mostly wall face and little roof, which reads fine for
        // irregular mountain silhouettes but clashes with the flat top-down
        // 2D terrain: buildings looked like they were "sticking out" at an
        // odd angle instead of sitting flush with the map. ~55° shows the
        // roofs (the more distinctive, colored part of these models) while
        // still keeping enough wall visible to read as 3D, not a flat icon.
        Vector3 viewDir = new Vector3(3f, 6f, 3f).Normalized();
        viewport.AddChild(camera);

        var bakes = new List<SettlementBake>();
        foreach (var (name, build, cameraSize, cameraCenter) in Styles)
        {
            var assembly = build();
            viewport.AddChild(assembly.Root);

            camera.Size = cameraSize * 1.3f;
            camera.Position = cameraCenter + viewDir * (camera.Size * 1.6f);
            camera.LookAt(cameraCenter, Vector3.Up);

            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

            var texture = ImageTexture.CreateFromImage(viewport.GetTexture().GetImage());
            bakes.Add(new SettlementBake(name, texture, assembly.Bounds.Size.X, assembly.Bounds.Size.Z));

            assembly.Root.QueueFree();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
        }

        viewport.QueueFree();
        return bakes;
    }

    // Accumulates a scene's real bounding box as pieces are added, instead of
    // a hand-guessed "size" constant per style.
    private sealed class Assembly
    {
        public readonly Node3D Root = new();
        public Aabb Bounds;
        private bool _any;

        public void Add(string path, Vector3 position, float rotationDegreesY = 0f)
        {
            var scene = GD.Load<PackedScene>(path);
            var instance = scene.Instantiate<Node3D>();
            instance.Position = position;
            instance.RotationDegrees = new Vector3(0f, rotationDegreesY, 0f);
            Root.AddChild(instance);

            Aabb local = FindFirstMeshAabb(instance) ?? new Aabb(Vector3.Zero, Vector3.Zero);
            var xform = new Transform3D(Basis.Identity.Rotated(Vector3.Up, Mathf.DegToRad(rotationDegreesY)), position);
            Aabb world = TransformAabb(local, xform);
            Bounds = _any ? Bounds.Merge(world) : world;
            _any = true;
        }

        private static Aabb? FindFirstMeshAabb(Node node)
        {
            if (node is MeshInstance3D mi) return mi.GetAabb();
            foreach (Node child in node.GetChildren())
            {
                var found = FindFirstMeshAabb(child);
                if (found is not null) return found;
            }
            return null;
        }

        private static Aabb TransformAabb(Aabb aabb, Transform3D xform)
        {
            Aabb result = new(xform * aabb.Position, Vector3.Zero);
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = aabb.Position + new Vector3(
                    (i & 1) != 0 ? aabb.Size.X : 0f,
                    (i & 2) != 0 ? aabb.Size.Y : 0f,
                    (i & 4) != 0 ? aabb.Size.Z : 0f);
                result = result.Expand(xform * corner);
            }
            return result;
        }
    }

    // ---- Town styles: clusters of distinct houses (City Kit Suburban) ----
    // Each house has a different footprint (~1.0-1.4 units), pivot at
    // ground center — spacing below (1.6-1.9 units) comes from those real
    // AABBs (dumped via a throwaway debug probe), sized to clear the
    // largest house without guessing.

    private static Assembly BuildVillage()
    {
        var a = new Assembly();
        a.Add("res://models/town/building-type-a.glb", Vector3.Zero);
        a.Add("res://models/town/building-type-r.glb", new Vector3(1.7f, 0f, 0.3f), rotationDegreesY: -20f);
        a.Add("res://models/town/tree-small.glb", new Vector3(-1f, 0f, 0.6f));
        return a;
    }

    private static Assembly BuildTown()
    {
        var a = new Assembly();
        a.Add("res://models/town/building-type-a.glb", Vector3.Zero);
        a.Add("res://models/town/building-type-c.glb", new Vector3(1.8f, 0f, 0.1f), rotationDegreesY: 10f);
        a.Add("res://models/town/building-type-j.glb", new Vector3(0.2f, 0f, 1.8f), rotationDegreesY: -15f);
        a.Add("res://models/town/building-type-p.glb", new Vector3(1.9f, 0f, 1.9f), rotationDegreesY: 25f);
        a.Add("res://models/town/tree-large.glb", new Vector3(-1.1f, 0f, 1.9f));
        a.Add("res://models/town/fence-1x2.glb", new Vector3(-0.6f, 0f, -0.9f), rotationDegreesY: 90f);
        return a;
    }

    private static Assembly BuildCity()
    {
        var a = new Assembly();
        string[] houses = { "a", "c", "f", "j", "p", "r" };
        float[] rotations = { 0f, 15f, -10f, 20f, -15f, 8f };
        for (int i = 0; i < houses.Length; i++)
        {
            float x = (i % 3) * 1.9f;
            float z = (i / 3) * 1.9f;
            a.Add($"res://models/town/building-type-{houses[i]}.glb", new Vector3(x, 0f, z), rotationDegreesY: rotations[i]);
        }
        a.Add("res://models/town/tree-large.glb", new Vector3(-1.1f, 0f, -1f));
        a.Add("res://models/town/tree-small.glb", new Vector3(4.9f, 0f, 2.9f));
        a.Add("res://models/town/path-short.glb", new Vector3(1.9f, 0f, 0.95f));
        return a;
    }

    // ---- Fortified styles: Castle Kit towers/walls ------------------------

    private static Assembly BuildHexagonKeep()
    {
        var a = new Assembly();
        a.Add("res://models/castle/tower-hexagon-base.glb", Vector3.Zero);
        a.Add("res://models/castle/tower-hexagon-mid.glb", new Vector3(0f, 1.31f, 0f));
        a.Add("res://models/castle/tower-hexagon-roof.glb", new Vector3(0f, 1.77f, 0f));
        return a;
    }

    private static Assembly BuildHighRoofKeep()
    {
        var a = new Assembly();
        AddSquareTower(a, Vector3.Zero, "res://models/castle/tower-square-top-roof-high.glb");
        return a;
    }

    private static Assembly BuildWalledKeep()
    {
        var a = new Assembly();
        AddSquareTower(a, Vector3.Zero, "res://models/castle/tower-square-top-roof.glb");
        a.Add("res://models/castle/wall.glb", new Vector3(1f, 0f, 0f));
        a.Add("res://models/castle/wall-corner.glb", new Vector3(2f, 0f, 0f));
        return a;
    }

    // A full fortress: four corner towers on a 3x3 perimeter, connected by
    // wall runs on all four sides, a gate on the front wall, and a flag —
    // this is the actual answer to "is there a castle", not just a keep
    // with one wall segment attached.
    private static Assembly BuildCastle()
    {
        var a = new Assembly();
        const string roof = "res://models/castle/tower-square-top-roof.glb";
        Vector3[] corners = { new(0, 0, 0), new(3, 0, 0), new(0, 0, 3), new(3, 0, 3) };
        foreach (var corner in corners)
            AddSquareTower(a, corner, roof);

        // Front and back wall runs (along X).
        foreach (float z in new[] { 0f, 3f })
            a.Add("res://models/castle/wall.glb", new Vector3(2f, 0f, z));

        // The front-wall gate opening (replaces the middle wall segment).
        a.Add("res://models/castle/gate.glb", new Vector3(1f, 0f, 0f));

        // Side wall runs (along Z), walls rotated to lie along Z.
        foreach (float x in new[] { 0f, 3f })
        {
            a.Add("res://models/castle/wall.glb", new Vector3(x, 0f, 1f), rotationDegreesY: 90f);
            a.Add("res://models/castle/wall.glb", new Vector3(x, 0f, 2f), rotationDegreesY: 90f);
        }

        a.Add("res://models/castle/flag.glb", new Vector3(0f, 3f, 0f));
        return a;
    }

    private static void AddSquareTower(Assembly a, Vector3 basePos, string roofPath)
    {
        a.Add("res://models/castle/tower-square-base.glb", basePos);
        a.Add("res://models/castle/tower-square-mid.glb", basePos + new Vector3(0f, 1f, 0f));
        a.Add(roofPath, basePos + new Vector3(0f, 2f, 0f));
    }
}
