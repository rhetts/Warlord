using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using Warlord.Core.State;
using Warlord.Core.Generation;

namespace Warlord.Game;

/// <summary>One baked mountain-cluster texture plus where it goes on the map.</summary>
public readonly record struct MountainBake(ImageTexture Texture, float CenterCol, float CenterRow, int ColSpan, int RowSpan);

/// <summary>
/// Renders one procedural heightfield mesh per contiguous mountain cluster
/// (see MapGeometry.ExtractMountainClusters) into an offscreen 3D viewport,
/// with a real directional light, and bakes each into its own 2D texture.
/// This keeps the campaign map itself a 2D scene (see docs/ARCHITECTURE.md)
/// while getting genuine 3D shading, and — because the heightfield's shape
/// follows the cluster's own cell footprint rather than a fixed small blob —
/// a whole mountain range reads as one connected chain instead of one
/// disconnected cone per cell.
/// </summary>
public static class MountainBaker
{
    private const float MaxHeight = 3.2f;
    private const int Pad = 2;             // extra empty cells around the cluster so it tapers to 0 before its edge
    private const float MarginFactor = 1.3f; // camera framing margin beyond the padded footprint
    private const float PxPerCell = 140f;
    private const int MinTextureSize = 1024; // even small clusters can fill the screen once zoomed in
    private const int MaxTextureSize = 3072;

    public static async Task<List<MountainBake>> BakeClustersAsync(Node host, List<List<Cell>> clusters, int seed)
    {
        var viewport = new SubViewport { TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
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
        viewport.AddChild(camera);

        Vector3 viewDir = new Vector3(4f, 3.2f, 4f).Normalized();
        var shader = GD.Load<Shader>("res://shaders/mountain_peak.gdshader");
        var rockTex = GD.Load<Texture2D>("res://textures/terrain/mountain_rock.jpg");
        var snowTex = GD.Load<Texture2D>("res://textures/terrain/mountain_snow.jpg");

        var bakes = new List<MountainBake>(clusters.Count);
        for (int i = 0; i < clusters.Count; i++)
        {
            var cluster = clusters[i];
            var bbox = BoundingBox(cluster);
            int paddedCols = bbox.ColSpan + 2 * Pad;
            int paddedRows = bbox.RowSpan + 2 * Pad;
            int longSpan = Mathf.Max(paddedCols, paddedRows);

            int pixels = (int)Mathf.Clamp(longSpan * PxPerCell, MinTextureSize, MaxTextureSize);
            viewport.Size = new Vector2I(pixels, pixels);

            camera.Size = longSpan + MaxHeight * 0.6f;
            camera.Size *= MarginFactor;
            camera.Position = viewDir * (camera.Size * 1.6f);
            camera.LookAt(Vector3.Zero, Vector3.Up);

            var mesh = BuildClusterHeightfield(shader, rockTex, snowTex, cluster, bbox, seed + i * 97);
            viewport.AddChild(mesh);

            // The bake happens on the GPU over subsequent frames; give it a
            // couple before reading the texture back.
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame);

            var texture = ImageTexture.CreateFromImage(viewport.GetTexture().GetImage());
            bakes.Add(new MountainBake(
                texture, (bbox.MinCol + bbox.MaxCol) / 2f, (bbox.MinRow + bbox.MaxRow) / 2f,
                paddedCols, paddedRows));

            mesh.QueueFree();
            await host.ToSignal(host.GetTree(), SceneTree.SignalName.ProcessFrame); // let the free land before reusing the viewport
        }

        viewport.QueueFree();
        return bakes;
    }

    private readonly record struct Bbox(int MinCol, int MaxCol, int MinRow, int MaxRow, int ColSpan, int RowSpan);

    private static Bbox BoundingBox(List<Cell> cluster)
    {
        int minCol = int.MaxValue, maxCol = int.MinValue, minRow = int.MaxValue, maxRow = int.MinValue;
        foreach (var cell in cluster)
        {
            minCol = Mathf.Min(minCol, cell.Col);
            maxCol = Mathf.Max(maxCol, cell.Col);
            minRow = Mathf.Min(minRow, cell.Row);
            maxRow = Mathf.Max(maxRow, cell.Row);
        }
        return new Bbox(minCol, maxCol, minRow, maxRow, maxCol - minCol + 1, maxRow - minRow + 1);
    }

    // A quad is only worth drawing once it's essentially got no presence at
    // all; the shader's own ALPHA fade (driven by the same mask, see below)
    // handles the visible soft edge, so this only needs to trim the truly
    // dead far-outside area, not shape the blend itself.
    private const float MaskCutoff = 0.03f;

    // A heightfield sized to the cluster's own padded bounding box. The mask
    // is sampled from the cluster's actual cell occupancy (blurred for a soft
    // edge) rather than a generic radial falloff, so the mesh's silhouette
    // follows the real shape of the mountain range — a long chain stays a
    // long chain instead of rounding into a blob. The mask is also carried
    // as vertex color so the shader can fade ALPHA smoothly at the rim,
    // blending into the surrounding 2D terrain instead of a hard edge.
    // Rolling + ridged noise on top (both via Warlord.Core's ValueNoise, same
    // as the coastline wobble) gives irregular ridgelines instead of one
    // smooth mound.
    private static Node3D BuildClusterHeightfield(
        Shader shader, Texture2D rockTex, Texture2D snowTex, List<Cell> cluster, Bbox bbox, int seed)
    {
        int paddedCols = bbox.ColSpan + 2 * Pad;
        int paddedRows = bbox.RowSpan + 2 * Pad;

        var occupancy = new float[paddedCols, paddedRows];
        foreach (var cell in cluster)
            occupancy[cell.Col - bbox.MinCol + Pad, cell.Row - bbox.MinRow + Pad] = 1f;
        occupancy = BoxBlur(occupancy, passes: 2);

        // Vertices-per-cell used to cap out at 140 total regardless of cluster
        // size, so density (not just vertex count) collapsed for big ranges —
        // a 40-cell-long range got the same resolution as a 10-cell one,
        // stretching each triangle over 4x the area. This is a one-time
        // load-time bake, so there's no real cost to just keeping density
        // constant instead of capping it away; 700 is only a sanity ceiling
        // (the biggest possible cluster on a 100x100 map is ~104 cells long).
        const int VerticesPerCell = 5;
        int longSpan = Mathf.Max(paddedCols, paddedRows);
        int gridRes = (int)Mathf.Clamp(longSpan * VerticesPerCell, 16, 700);
        int resX = (int)Mathf.Clamp(Mathf.Round((float)paddedCols / longSpan * gridRes), 4, gridRes);
        int resZ = (int)Mathf.Clamp(Mathf.Round((float)paddedRows / longSpan * gridRes), 4, gridRes);

        var noise = new ValueNoise(seed);
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);

        var maskGrid = new float[resX, resZ];
        float peakHeight = 0f;
        for (int j = 0; j < resZ; j++)
        {
            for (int i = 0; i < resX; i++)
            {
                // Position in padded-cell space, [0, paddedCols] x [0, paddedRows].
                float gx = i / (float)(resX - 1) * paddedCols;
                float gz = j / (float)(resZ - 1) * paddedRows;
                float mask = SampleBilinear(occupancy, gx, gz);
                maskGrid[i, j] = mask;
                // Pow < 1 pulls mid mask values up toward 1, so the range's
                // edge climbs to full height over a shorter distance — a
                // steeper apron instead of a gradual talus slope.
                float steepMask = Mathf.Pow(Mathf.Clamp(mask, 0f, 1f), 0.55f);

                float wx = gx - paddedCols / 2f;
                float wz = gz - paddedRows / 2f;
                float rolling = noise.Fractal(wx * 0.7f, wz * 0.7f, 4);
                float ridges = RidgedFractal(noise, wx * 1.3f, wz * 1.3f, 4);
                float relief = rolling * 0.25f + ridges * 1.2f;

                float height = steepMask * MaxHeight * (0.15f + relief * 1.1f);
                peakHeight = Mathf.Max(peakHeight, height);

                st.SetColor(new Color(mask, mask, mask, 1f));
                st.AddVertex(new Vector3(wx, height, wz));
            }
        }

        for (int j = 0; j < resZ - 1; j++)
        {
            for (int i = 0; i < resX - 1; i++)
            {
                float corner = Mathf.Max(
                    Mathf.Max(maskGrid[i, j], maskGrid[i + 1, j]),
                    Mathf.Max(maskGrid[i, j + 1], maskGrid[i + 1, j + 1]));
                if (corner < MaskCutoff) continue;

                int idx00 = j * resX + i;
                int idx10 = j * resX + i + 1;
                int idx01 = (j + 1) * resX + i;
                int idx11 = (j + 1) * resX + i + 1;

                // Winding gives an upward-facing (camera-visible, non-culled)
                // normal for a Y-up heightfield — verified by screenshot, not
                // just derived by hand: the opposite order rendered as almost
                // nothing but stray silhouette slivers, since cull_back was
                // culling literally every top-facing triangle.
                st.AddIndex(idx00); st.AddIndex(idx10); st.AddIndex(idx01);
                st.AddIndex(idx10); st.AddIndex(idx11); st.AddIndex(idx01);
            }
        }

        st.GenerateNormals();

        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("peak_height", Mathf.Max(peakHeight, 0.1f));
        material.SetShaderParameter("rock_tex", rockTex);
        material.SetShaderParameter("snow_tex", snowTex);

        var meshInstance = new MeshInstance3D { Mesh = st.Commit(), MaterialOverride = material };
        var root = new Node3D();
        root.AddChild(meshInstance);
        return root;
    }

    private static float SampleBilinear(float[,] grid, float x, float y)
    {
        int w = grid.GetLength(0), h = grid.GetLength(1);
        float cx = Mathf.Clamp(x, 0f, w - 1f);
        float cy = Mathf.Clamp(y, 0f, h - 1f);
        int x0 = Mathf.Min((int)cx, w - 2 < 0 ? 0 : w - 2);
        int y0 = Mathf.Min((int)cy, h - 2 < 0 ? 0 : h - 2);
        int x1 = Mathf.Min(x0 + 1, w - 1);
        int y1 = Mathf.Min(y0 + 1, h - 1);
        float fx = cx - x0, fy = cy - y0;

        float top = Mathf.Lerp(grid[x0, y0], grid[x1, y0], fx);
        float bottom = Mathf.Lerp(grid[x0, y1], grid[x1, y1], fx);
        return Mathf.Lerp(top, bottom, fy);
    }

    private static float[,] BoxBlur(float[,] grid, int passes)
    {
        int w = grid.GetLength(0), h = grid.GetLength(1);
        for (int p = 0; p < passes; p++)
        {
            var next = new float[w, h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                            sum += grid[nx, ny];
                            count++;
                        }
                    }
                    next[x, y] = sum / count;
                }
            }
            grid = next;
        }
        return grid;
    }

    // Standard ridged-multifractal trick: fold each octave's value noise
    // around its midpoint (1 - |2v-1|) so it peaks at sharp ridgelines instead
    // of smooth bumps, then raise to the 4th power (not just squared) for
    // narrower, pointier cusps instead of rounded ridge crests.
    private static float RidgedFractal(
        ValueNoise noise, float x, float y, int octaves, float persistence = 0.55f, float lacunarity = 2f)
    {
        float total = 0f, amplitude = 1f, frequency = 1f, maxAmplitude = 0f;
        for (int i = 0; i < octaves; i++)
        {
            float v = noise.Value(x * frequency, y * frequency);
            float ridge = 1f - Mathf.Abs(2f * v - 1f);
            ridge *= ridge;
            ridge *= ridge;
            total += ridge * amplitude;
            maxAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        return total / maxAmplitude;
    }
}
