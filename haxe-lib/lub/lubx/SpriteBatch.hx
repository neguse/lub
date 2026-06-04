package lubx;

import lub.Gfx;

typedef SpriteBucket = {atlas:Atlas, verts:Array<Float>, ready:Bool};

class SpriteBatch {
	public static inline var STRIDE:Int = 8;

	static var VS:String = "struct Uniforms { float4 params; };\n"
		+ "ConstantBuffer<Uniforms> u;\n"
		+ "struct VSIn  { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };\n"
		+ "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 color : COLOR; };\n"
		+ "[shader(\"vertex\")]\n"
		+ "VSOut vs_main(VSIn i) {\n"
		+ "    VSOut o;\n"
		+ "    float2 p = float2(i.pos.x / u.params.x * 2.0 - 1.0, 1.0 - i.pos.y / u.params.y * 2.0);\n"
		+ "    o.pos = float4(p, 0.0, 1.0);\n"
		+ "    o.uv = i.uv;\n"
		+ "    o.color = i.color;\n"
		+ "    return o;\n"
		+ "}\n";

	static var FS:String = "LUB_TEXTURE2D(atlas);\n"
		+ "struct FSIn { float2 uv : TEXCOORD0; float4 color : COLOR; };\n"
		+ "[shader(\"fragment\")]\n"
		+ "float4 fs_main(FSIn i) : SV_Target {\n"
		+ "    float4 c = LUB_SAMPLE(atlas, i.uv) * i.color;\n"
		+ "    if (c.a < 0.004) discard;\n"
		+ "    return c;\n"
		+ "}\n";

	public var logicalW:Int;
	public var logicalH:Int;

	final buckets = new Map<String, SpriteBucket>();
	final order = new Array<String>();
	final shaderKey:String;
	final bufferPrefix:String;
	var shader:Dynamic = null;
	var meshVersion:Int = 0;

	public function new(logicalW:Int, logicalH:Int, shaderKey:String = "lubx_sprite", bufferPrefix:String = "lubx_sprite") {
		this.logicalW = logicalW;
		this.logicalH = logicalH;
		this.shaderKey = shaderKey;
		this.bufferPrefix = bufferPrefix;
	}

	public function ensure():Bool {
		shader = Gfx.useShader(shaderKey, VS, FS, 1);
		return shader != null;
	}

	public function begin() {
		for (k in order) {
			var b = buckets.get(k);
			b.verts.resize(0);
			b.ready = false;
		}
	}

	function bucketFor(a:Atlas):Array<Float> {
		var b = buckets.get(a.key);
		if (b == null) {
			b = {atlas: a, verts: [], ready: false};
			buckets.set(a.key, b);
			order.push(a.key);
		}
		if (!b.ready) {
			if (!a.ensure())
				return null;
			b.ready = true;
		}
		return b.verts;
	}

	inline function colorOrWhite(c:Color):Color {
		if (c != null)
			return c;
		return {r: 1.0, g: 1.0, b: 1.0, a: 1.0};
	}

	inline function push(out:Array<Float>, x:Float, y:Float, u:Float, v:Float, c:Color) {
		out.push(x);
		out.push(y);
		out.push(u);
		out.push(v);
		out.push(c.r);
		out.push(c.g);
		out.push(c.b);
		out.push(c.a);
	}

	inline function pushRot(out:Array<Float>, cx:Float, cy:Float, ox:Float, oy:Float, cr:Float, sr:Float, u:Float, v:Float, c:Color) {
		push(out, cx + ox * cr - oy * sr, cy + ox * sr + oy * cr, u, v, c);
	}

	public function sprite(a:Atlas, src:Rect, cx:Float, cy:Float, w:Float, h:Float, radians:Float, ?tint:Color) {
		var out = bucketFor(a);
		if (out == null)
			return;

		var c = colorOrWhite(tint);
		var u0 = src.x / a.w;
		var v0 = src.y / a.h;
		var u1 = (src.x + src.w) / a.w;
		var v1 = (src.y + src.h) / a.h;
		var hw = w * 0.5;
		var hh = h * 0.5;
		var cr = Math.cos(radians);
		var sr = Math.sin(radians);

		pushRot(out, cx, cy, -hw, -hh, cr, sr, u0, v0, c);
		pushRot(out, cx, cy, hw, -hh, cr, sr, u1, v0, c);
		pushRot(out, cx, cy, hw, hh, cr, sr, u1, v1, c);
		pushRot(out, cx, cy, -hw, -hh, cr, sr, u0, v0, c);
		pushRot(out, cx, cy, hw, hh, cr, sr, u1, v1, c);
		pushRot(out, cx, cy, -hw, hh, cr, sr, u0, v1, c);
	}

	public function quad(a:Atlas, src:Rect, x:Float, y:Float, w:Float, h:Float, ?tint:Color) {
		var out = bucketFor(a);
		if (out == null)
			return;

		var c = colorOrWhite(tint);
		var u0 = src.x / a.w;
		var v0 = src.y / a.h;
		var u1 = (src.x + src.w) / a.w;
		var v1 = (src.y + src.h) / a.h;
		var x1 = x + w;
		var y1 = y + h;

		push(out, x, y, u0, v0, c);
		push(out, x1, y, u1, v0, c);
		push(out, x1, y1, u1, v1, c);
		push(out, x, y, u0, v0, c);
		push(out, x1, y1, u1, v1, c);
		push(out, x, y1, u0, v1, c);
	}

	public function flush(blend:Int = -1) {
		if (!ensure())
			return;

		meshVersion = meshVersion + 1;
		var params = lua.Table.fromArray([logicalW, logicalH, 0.0, 0.0]);
		var blendMode = (blend < 0) ? Gfx.ALPHA : blend;
		for (k in order) {
			var b = buckets.get(k);
			if (b.verts.length == 0)
				continue;
			var vbuf = Gfx.useBuffer(bufferPrefix + "_" + k, Gfx.VERTEX, lua.Table.fromArray(b.verts), meshVersion);
			Gfx.draw(Std.int(b.verts.length / STRIDE), {
				verts: vbuf,
				atlas: b.atlas.texture,
				uniforms: {params: params}
			}, {
				shader: shader,
				depth: false,
				cull: Gfx.NONE,
				blend: blendMode
			});
		}
	}
}
