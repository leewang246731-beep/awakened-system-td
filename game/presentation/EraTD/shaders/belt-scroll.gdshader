// T-119 (C-28/F-127): conveyor-belt dual-layer scroll shader (直线段 UV 滚动).
//
// Straight belt cells draw ONE 32 px quad whose pixels come from two stacked
// 16 px layers (exact 2x nearest-filtered integer scale, same render baseline
// as terrain-atlas.gdshader — no resampling, no mipmaps):
//
//   layer 1  body  — the ANIMATED belt surface: the pattern period is 16 px
//                   (= body tile edge), so offsetting V by -scroll_px/16 and
//                   sampling with repeat_enable scrolls the surface along the
//                   travel direction with zero frame-cache cost (F-127:
//                   直线段用 shader 滚动不耗帧缓存). The scroll value comes
//                   from the frozen EraCore decision layer:
//                   BeltRenderDecisions.ShaderOffsetPx = (scroll − origin)
//                   mod 16, where the per-cell origin re-locks the pattern
//                   across turns (a turn advances the path 21 px = period
//                   16 + 5, so downstream cells shift phase by 5) and
//                   neighbouring cells.
//   layer 2  edge  — the STATIC riveted side rails sampled at plain UV: the
//                   border never crawls while the body flows (双层拆分:
//                   动画带体 + 静态边框).
//
// Canonical orientation: the tile travels SOUTH (+V, entry at the north
// edge); body texture row v carries pattern phase v, so display row y shows
// pattern (y − scroll) — the surface flows downstream (+V) as scroll grows.
// Per-instance data (MultiMesh, ground-layer precedent): INSTANCE_CUSTOM.y
// carries the clockwise quarter-turn rotation (0..3) computed by
// BeltRenderDecisions.SpriteRotationQuarters — the rotation turns the WHOLE
// two-layer stack (body flow + edge rails stay glued). Turn cells do not use
// this shader at all: they render the 8-frame belt-turn-anim strip +
// belt-turn-edge overlay driven by the SAME phase quantity
// (BeltRenderDecisions.FrameIndexFor = floor(phase / 2), straight offset =
// phase) — belt-scroll.gdshader is the STRAIGHT half of the F-127 split.
shader_type canvas_item;

uniform sampler2D body : source_color, filter_nearest, repeat_enable;
uniform sampler2D edge : source_color, filter_nearest;
// Surface scroll in 16 px body-tile space (px) = BeltRenderDecisions
// .ShaderOffsetPx output; advance = belt surface speed 16 px/tick
// (TransportTiers.Belt) × render time, re-based per cell origin.
uniform float scroll_px = 0.0;

varying vec2 tile_uv; // rotated per-instance UV (canonical south-travel frame)

void vertex() {
	// Quarter turns clockwise about the tile centre; the builtin lives in
	// the vertex stage only (terrain-atlas precedent: INSTANCE_CUSTOM).
	float q = floor(INSTANCE_CUSTOM.y + 0.5);
	vec2 c = UV - vec2(0.5);
	vec2 r;
	if (q < 0.5) {
		r = c;                       // 0: south (canonical travel +V)
	} else if (q < 1.5) {
		r = vec2(c.y, -c.x);         // 1: image rotated CW (travel west)
	} else if (q < 2.5) {
		r = -c;                      // 2: 180° (travel north)
	} else {
		r = vec2(-c.y, c.x);         // 3: image rotated CCW (travel east)
	}
	tile_uv = r + vec2(0.5);
}

void fragment() {
	// Layer 1 — animated body: sampling V ABOVE the display position makes
	// the pattern flow +V (canonical south) as scroll_px grows; repeat_enable
	// wraps the 16 px period seamlessly.
	vec2 body_uv = tile_uv - vec2(0.0, scroll_px / 16.0);
	vec4 body_px = texture(body, body_uv);

	// Layer 2 — static edge overlay (rails + rivets), same rotation, no flow.
	vec4 edge_px = texture(edge, tile_uv);

	// Dual-layer over-composite (edge over body); both source layers are
	// 0/255 alpha pixel art, so this is an exact binary split.
	float out_a = edge_px.a + body_px.a * (1.0 - edge_px.a);
	COLOR = vec4(
		(edge_px.rgb * edge_px.a + body_px.rgb * body_px.a * (1.0 - edge_px.a))
		/ max(out_a, 0.0001),
		out_a);
}
