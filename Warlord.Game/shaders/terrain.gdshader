shader_type canvas_item;

// Terrain fill shader.
//
// The land is drawn as the smoothed coastline polygon (rounded shape). Its
// color comes from two per-cell lookup textures sampled by UV (0..1 across the
// whole map), with linear filtering so terrain types blend softly between cells.
// Noise adds organic variation so flat regions aren't dead flat.

uniform sampler2D terrain_tex : filter_linear, repeat_disable;
uniform sampler2D faction_tex : filter_linear, repeat_disable;
uniform float noise_scale = 40.0;

// Real photo textures (CC0, ambientCG), one per non-water terrain type,
// tiled densely across the map. terrain_tex's flat per-type color still
// exists (it's what IsoMap bakes per cell) and now does double duty as a
// color key: each fragment's sampled color is compared against the five
// known reference colors below to get soft per-type blend weights, so detail
// texture selection follows the same cell-boundary blending terrain_tex
// already has from linear filtering — no separate per-cell type lookup needed.
uniform sampler2D coast_tex : filter_linear, repeat_enable;
uniform sampler2D plains_tex : filter_linear, repeat_enable;
uniform sampler2D forest_tex : filter_linear, repeat_enable;
uniform sampler2D hills_tex : filter_linear, repeat_enable;
uniform sampler2D mountains_tex : filter_linear, repeat_enable;
uniform float detail_tile_scale = 60.0;

const vec3 COAST_COLOR = vec3(0.7961, 0.7137, 0.5098);
const vec3 PLAINS_COLOR = vec3(0.4784, 0.6510, 0.3255);
const vec3 FOREST_COLOR = vec3(0.1843, 0.4196, 0.2275);
const vec3 HILLS_COLOR = vec3(0.6118, 0.4980, 0.3059);
const vec3 MOUNTAINS_COLOR = vec3(0.5608, 0.5608, 0.5608);

// Fake hillshading: terrain_tex.a carries a rough 0..1 elevation per cell (see
// IsoMap.TerrainHeight). Its screen-space derivative approximates a slope, lit
// from a fixed direction — cheap raking-light look with no real 3D geometry.
// First pass — flip light_dir or retune strength once we've seen it in-game.
uniform vec2 light_dir = vec2(0.7, 0.7);
uniform float hillshade_strength = 6.0;

float hash(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++)
    {
        v += a * vnoise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

// Inverse-distance weight against a known reference color: near 1 when the
// sampled flat color matches that terrain type, falling off smoothly for
// others so mixed/boundary colors blend multiple detail textures together.
float colorWeight(vec3 sampled, vec3 reference)
{
    return 1.0 / (distance(sampled, reference) + 0.02);
}

vec2 rotateUv(vec2 uv, float angle)
{
    float s = sin(angle);
    float c = cos(angle);
    vec2 centered = uv - 0.5;
    return vec2(centered.x * c - centered.y * s, centered.x * s + centered.y * c) + 0.5;
}

// Breaks up obvious tiling: sample the same texture a second time at an
// unrelated scale/rotation/offset (so its repeat period never lines up with
// the first sample's) and cross-blend by a large-patch noise value. No two
// visible patches end up showing the exact same repeating pattern.
vec3 sampleDetail(sampler2D tex, vec2 tiledUv, float patchNoise)
{
    vec3 a = texture(tex, tiledUv).rgb;
    vec2 uvB = rotateUv(tiledUv * 1.7, 2.399) + vec2(0.61, 0.27);
    vec3 b = texture(tex, uvB).rgb;
    return mix(a, b, patchNoise);
}

void fragment()
{
    vec4 terrain = texture(terrain_tex, UV);
    float n = fbm(UV * noise_scale);
    float shade = 0.82 + 0.32 * n;

    float height = terrain.a;
    vec2 gradH = vec2(dFdx(height), dFdy(height));
    shade += dot(gradH, light_dir) * hillshade_strength;
    shade = clamp(shade, 0.15, 1.8);

    vec2 tiledUv = UV * detail_tile_scale;
    // Much lower frequency than the fine shading noise above, so the
    // tiling-breakup blend shifts over large patches, not per-pixel.
    float patchNoise = fbm(UV * 6.0);

    float wCoast = colorWeight(terrain.rgb, COAST_COLOR);
    float wPlains = colorWeight(terrain.rgb, PLAINS_COLOR);
    float wForest = colorWeight(terrain.rgb, FOREST_COLOR);
    float wHills = colorWeight(terrain.rgb, HILLS_COLOR);
    float wMountains = colorWeight(terrain.rgb, MOUNTAINS_COLOR);
    float wSum = wCoast + wPlains + wForest + wHills + wMountains;

    vec3 detail = (
        sampleDetail(coast_tex, tiledUv, patchNoise) * wCoast +
        sampleDetail(plains_tex, tiledUv, patchNoise) * wPlains +
        sampleDetail(forest_tex, tiledUv, patchNoise) * wForest +
        sampleDetail(hills_tex, tiledUv, patchNoise) * wHills +
        sampleDetail(mountains_tex, tiledUv, patchNoise) * wMountains
    ) / wSum;

    vec3 col = detail * shade;

    // Translucent faction ownership tint.
    vec4 faction = texture(faction_tex, UV);
    col = mix(col, faction.rgb, faction.a);

    COLOR = vec4(col, 1.0);
}
