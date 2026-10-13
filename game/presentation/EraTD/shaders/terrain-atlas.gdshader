// T-045 (C-21/C-28): terrain atlas sampler for the ground MultiMesh layer.
//
// One MultiMesh draws every ground cell with the SAME QuadMesh, so the tile
// choice is per-instance data: MultiMeshRenderer writes the tile index into
// INSTANCE_CUSTOM.x (vertex stage — the builtin does not exist in fragment)
// and the vertex shader maps the quad's 0..1 UV into the matching 16 px
// atlas cell. The 16 px tile on a 32 px cell is an exact 2x integer scale;
// `filter_nearest` keeps every texel a crisp 2x2 px block
// (14-art-license-audit §3 render baseline — no resampling, no mipmaps).
shader_type canvas_item;

uniform sampler2D atlas : source_color, filter_nearest;
uniform int tiles_per_row = 8;

varying vec2 atlas_uv;

void vertex() {
	float index = floor(INSTANCE_CUSTOM.x + 0.5);
	float n = float(tiles_per_row);
	vec2 tile = vec2(mod(index, n), floor(index / n));
	atlas_uv = (tile + UV) / n;
}

void fragment() {
	COLOR *= texture(atlas, atlas_uv);
}
