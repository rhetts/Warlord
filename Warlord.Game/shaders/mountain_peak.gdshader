shader_type spatial;
render_mode cull_back, diffuse_lambert, specular_disabled, blend_mix;

// Blends rock -> snow near each peak's own tip, using real CC0 photo
// textures (ambientCG) tiled by world position instead of flat colors.
// peak_height is set per MeshInstance from C# (see MountainBaker) so
// different peaks in the same cluster each get a proportional cap instead of
// one shared world-space threshold that would leave short peaks bare.
// VERTEX.y is the mesh's raw (non-centered) height, 0 at the base plane.
uniform sampler2D rock_tex : filter_linear, repeat_enable;
uniform sampler2D snow_tex : filter_linear, repeat_enable;
uniform float detail_tile_scale = 2.0;
// The rock photo texture is notably lighter than the old flat rock color;
// darken it back down so peaks keep contrast against snow and terrain
// instead of reading as one flat pale mass.
uniform vec3 rock_tint : source_color = vec3(0.55, 0.55, 0.55);
uniform float peak_height = 1.8;
uniform float snow_start = 0.55; // fraction of height where snow begins
uniform float snow_full = 0.85;  // fraction of height where snow is solid

// Vertex color carries the region-occupancy mask (see MountainBaker), 1 deep
// inside the cluster fading toward 0 past its edge. The mesh itself is
// already cut off well outside the cluster (MountainBaker skips quads below
// MaskCutoff), so this only has to fade the surviving rim quads smoothly
// into the surrounding 2D terrain instead of ending in a hard silhouette.
varying float local_height;
varying float v_mask;
varying vec2 tiled_uv;

void vertex() {
    local_height = VERTEX.y;
    v_mask = COLOR.r;
    tiled_uv = VERTEX.xz * detail_tile_scale;
}

void fragment() {
    float t = smoothstep(snow_start, snow_full, local_height / peak_height);
    vec3 rockSample = texture(rock_tex, tiled_uv).rgb * rock_tint;
    vec3 snowSample = texture(snow_tex, tiled_uv).rgb;
    ALBEDO = mix(rockSample, snowSample, t);
    ROUGHNESS = 0.95;
    ALPHA = smoothstep(0.05, 0.5, v_mask);
}
