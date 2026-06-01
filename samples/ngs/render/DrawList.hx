package render;

import lub.Gfx;

class DrawList {
	public static inline var STRIDE:Int = 8;

	final buckets = new Map<String, {atlas:Atlas, verts:Array<Float>}>();
	final order = new Array<String>(); // 挿入順を保ち draw 順を決定的に

	public var shader:Dynamic;

	var meshVersion:Int = 0;

	public function new(shader:Dynamic) {
		this.shader = shader;
	}

	public function begin() {
		for (k in order)
			buckets.get(k).verts.resize(0);
	}

	inline function bucketFor(a:Atlas):Array<Float> {
		var b = buckets.get(a.key);
		if (b == null) {
			b = {atlas: a, verts: []};
			buckets.set(a.key, b);
			order.push(a.key);
		}
		return b.verts;
	}

	inline function vtx(out:Array<Float>, x:Float, y:Float, u:Float, v:Float, c:Color) {
		out.push(x);
		out.push(y);
		out.push(u);
		out.push(v);
		out.push(c.r);
		out.push(c.g);
		out.push(c.b);
		out.push(c.a);
	}

	// src = atlas 内 pixel rect、dst = logical 画面 pixel での左上 (dx,dy)。
	public function sprite(a:Atlas, src:Rect, dx:Int, dy:Int, ?tint:Color) {
		var c = (tint == null) ? {
			r: 1.0,
			g: 1.0,
			b: 1.0,
			a: 1.0
		} : tint;
		var u0 = src.x / a.w, v0 = src.y / a.h;
		var u1 = (src.x + src.w) / a.w, v1 = (src.y + src.h) / a.h;
		var x0 = dx, y0 = dy, x1 = dx + src.w, y1 = dy + src.h;
		var out = bucketFor(a);
		vtx(out, x0, y0, u0, v0, c);
		vtx(out, x1, y0, u1, v0, c);
		vtx(out, x1, y1, u1, v1, c);
		vtx(out, x0, y0, u0, v0, c);
		vtx(out, x1, y1, u1, v1, c);
		vtx(out, x0, y1, u0, v1, c);
	}

	// 単色矩形。white atlas (1x1) を使い uv 全域。
	public function quad(white:Atlas, x:Int, y:Int, w:Int, h:Int, c:Color) {
		var out = bucketFor(white);
		vtx(out, x, y, 0.5, 0.5, c);
		vtx(out, x + w, y, 0.5, 0.5, c);
		vtx(out, x + w, y + h, 0.5, 0.5, c);
		vtx(out, x, y, 0.5, 0.5, c);
		vtx(out, x + w, y + h, 0.5, 0.5, c);
		vtx(out, x, y + h, 0.5, 0.5, c);
	}

	public function flush() {
		meshVersion = meshVersion + 1;
		for (k in order) {
			var b = buckets.get(k);
			if (b.verts.length == 0)
				continue;
			var vbuf = Gfx.useBuffer("ngs_dl_" + k, Gfx.VERTEX, lua.Table.fromArray(b.verts), meshVersion);
			Gfx.draw(Std.int(b.verts.length / STRIDE), {verts: vbuf, atlas: b.atlas.texture}, {
				shader: shader,
				depth: false,
				cull: Gfx.NONE,
				blend: Gfx.ALPHA
			});
		}
	}
}
