package lubx;

import lub.Gfx;

typedef SpriteBucket = {atlas:Atlas, verts:Array<Float>, ready:Bool};

class SpriteBatch {
	public static inline var LEGACY_STRIDE:Int = 8;
	public static inline var VERTEX_STRIDE:Int = 4;
	public static inline var INSTANCE_STRIDE:Int = 14;

	static var LEGACY_VS:String = "struct Uniforms { float4 params; };\n"
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

	static var INSTANCED_VS:String = "struct Uniforms { float4 params; };\n"
		+ "ConstantBuffer<Uniforms> u;\n"
		+ "struct VSVertex { float2 corner : POSITION; float2 uv01 : TEXCOORD0; };\n"
		+
		"struct VSInstance { float2 pos : TEXCOORD1; float2 size : TEXCOORD2; float2 rot_cs : TEXCOORD3; float4 uv_rect : TEXCOORD4; float4 color : TEXCOORD5; };\n"
		+ "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; float4 color : COLOR; };\n"
		+ "[shader(\"vertex\")]\n"
		+ "VSOut vs_main(VSVertex v, VSInstance i) {\n"
		+ "    VSOut o;\n"
		+ "    float2 local = v.corner * i.size;\n"
		+ "    float2 p2 = i.pos + float2(local.x * i.rot_cs.x - local.y * i.rot_cs.y, local.x * i.rot_cs.y + local.y * i.rot_cs.x);\n"
		+ "    float2 p = float2(p2.x / u.params.x * 2.0 - 1.0, 1.0 - p2.y / u.params.y * 2.0);\n"
		+ "    o.pos = float4(p, 0.0, 1.0);\n"
		+ "    o.uv = lerp(i.uv_rect.xy, i.uv_rect.zw, v.uv01);\n"
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
	final instanced:Bool;
	var shader:Dynamic = null;
	var quadBuf:Dynamic = null;
	var quadData:Dynamic = null;
	var meshVersion:Int = 0;

	public function new(logicalW:Int, logicalH:Int, shaderKey:String = "lubx_sprite", bufferPrefix:String = "lubx_sprite", instanced:Bool = true) {
		this.logicalW = logicalW;
		this.logicalH = logicalH;
		this.shaderKey = shaderKey + (instanced ? "_instanced" : "_legacy");
		this.bufferPrefix = bufferPrefix;
		this.instanced = instanced;
	}

	public function ensure():Bool {
		shader = Gfx.useShader(shaderKey, instanced ? INSTANCED_VS : LEGACY_VS, FS, 1);
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
		return {
			r: 1.0,
			g: 1.0,
			b: 1.0,
			a: 1.0
		};
	}

	inline function pushInstance(out:Array<Float>, cx:Float, cy:Float, w:Float, h:Float, cr:Float, sr:Float, u0:Float, v0:Float, u1:Float, v1:Float, c:Color) {
		pushInstanceColor(out, cx, cy, w, h, cr, sr, u0, v0, u1, v1, c.r, c.g, c.b, c.a);
	}

	inline function pushInstanceColor(out:Array<Float>, cx:Float, cy:Float, w:Float, h:Float, cr:Float, sr:Float, u0:Float, v0:Float, u1:Float, v1:Float,
			r:Float, g:Float, b:Float, alpha:Float) {
		var i = out.length;
		untyped out[i] = cx;
		untyped out[i + 1] = cy;
		untyped out[i + 2] = w;
		untyped out[i + 3] = h;
		untyped out[i + 4] = cr;
		untyped out[i + 5] = sr;
		untyped out[i + 6] = u0;
		untyped out[i + 7] = v0;
		untyped out[i + 8] = u1;
		untyped out[i + 9] = v1;
		untyped out[i + 10] = r;
		untyped out[i + 11] = g;
		untyped out[i + 12] = b;
		untyped out[i + 13] = alpha;
		untyped out.length = i + INSTANCE_STRIDE;
	}

	inline function pushVertex(out:Array<Float>, x:Float, y:Float, u:Float, v:Float, c:Color) {
		pushVertexColor(out, x, y, u, v, c.r, c.g, c.b, c.a);
	}

	inline function pushVertexColor(out:Array<Float>, x:Float, y:Float, u:Float, v:Float, r:Float, g:Float, b:Float, alpha:Float) {
		var i = out.length;
		untyped out[i] = x;
		untyped out[i + 1] = y;
		untyped out[i + 2] = u;
		untyped out[i + 3] = v;
		untyped out[i + 4] = r;
		untyped out[i + 5] = g;
		untyped out[i + 6] = b;
		untyped out[i + 7] = alpha;
		untyped out.length = i + LEGACY_STRIDE;
	}

	inline function pushRot(out:Array<Float>, cx:Float, cy:Float, ox:Float, oy:Float, cr:Float, sr:Float, u:Float, v:Float, c:Color) {
		pushVertex(out, cx + ox * cr - oy * sr, cy + ox * sr + oy * cr, u, v, c);
	}

	inline function pushRotColor(out:Array<Float>, cx:Float, cy:Float, ox:Float, oy:Float, cr:Float, sr:Float, u:Float, v:Float, r:Float, g:Float, b:Float,
			alpha:Float) {
		pushVertexColor(out, cx + ox * cr - oy * sr, cy + ox * sr + oy * cr, u, v, r, g, b, alpha);
	}

	public function sprite(a:Atlas, src:Rect, cx:Float, cy:Float, w:Float, h:Float, radians:Float, ?tint:Color) {
		var c = colorOrWhite(tint);
		spriteColor(a, src, cx, cy, w, h, Math.cos(radians), Math.sin(radians), c.r, c.g, c.b, c.a);
	}

	public function spriteColor(a:Atlas, src:Rect, cx:Float, cy:Float, w:Float, h:Float, cr:Float, sr:Float, r:Float, g:Float, b:Float, alpha:Float) {
		var out = bucketFor(a);
		if (out == null)
			return;

		var u0 = src.x / a.w;
		var v0 = src.y / a.h;
		var u1 = (src.x + src.w) / a.w;
		var v1 = (src.y + src.h) / a.h;
		if (instanced) {
			pushInstanceColor(out, cx, cy, w, h, cr, sr, u0, v0, u1, v1, r, g, b, alpha);
			return;
		}

		var hw = w * 0.5;
		var hh = h * 0.5;
		pushRotColor(out, cx, cy, -hw, -hh, cr, sr, u0, v0, r, g, b, alpha);
		pushRotColor(out, cx, cy, hw, -hh, cr, sr, u1, v0, r, g, b, alpha);
		pushRotColor(out, cx, cy, hw, hh, cr, sr, u1, v1, r, g, b, alpha);
		pushRotColor(out, cx, cy, -hw, -hh, cr, sr, u0, v0, r, g, b, alpha);
		pushRotColor(out, cx, cy, hw, hh, cr, sr, u1, v1, r, g, b, alpha);
		pushRotColor(out, cx, cy, -hw, hh, cr, sr, u0, v1, r, g, b, alpha);
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
		if (instanced) {
			pushInstance(out, x + w * 0.5, y + h * 0.5, w, h, 1.0, 0.0, u0, v0, u1, v1, c);
			return;
		}

		var x1 = x + w;
		var y1 = y + h;
		pushVertex(out, x, y, u0, v0, c);
		pushVertex(out, x1, y, u1, v0, c);
		pushVertex(out, x1, y1, u1, v1, c);
		pushVertex(out, x, y, u0, v0, c);
		pushVertex(out, x1, y1, u1, v1, c);
		pushVertex(out, x, y1, u0, v1, c);
	}

	function ensureQuad():Dynamic {
		if (quadData == null)
			quadData = lua.Table.fromArray([
				-0.5, -0.5, 0.0, 0.0,
				 0.5, -0.5, 1.0, 0.0,
				-0.5,  0.5, 0.0, 1.0,
				 0.5,  0.5, 1.0, 1.0
			]);
		quadBuf = Gfx.useBuffer(bufferPrefix + "_quad", Gfx.VERTEX, quadData, 1);
		return quadBuf;
	}

	public function flush(blend:Int = -1) {
		if (!ensure())
			return;
		var quad = instanced ? ensureQuad() : null;
		if (instanced && quad == null)
			return;

		meshVersion = meshVersion + 1;
		var params = lua.Table.fromArray([logicalW, logicalH, 0.0, 0.0]);
		var blendMode = (blend < 0) ? Gfx.ALPHA : blend;
		for (k in order) {
			var b = buckets.get(k);
			if (b.verts.length == 0)
				continue;
			if (!instanced) {
				var vbuf = Gfx.useBuffer(bufferPrefix + "_" + k + "_verts", Gfx.VERTEX, b.verts, meshVersion);
				Gfx.draw(Std.int(b.verts.length / LEGACY_STRIDE), {
					verts: vbuf,
					atlas: b.atlas.texture,
					uniforms: {params: params}
				}, {
					shader: shader,
					depth: false,
					cull: Gfx.NONE,
					blend: blendMode
				});
				continue;
			}
			var instances = Gfx.useBuffer(bufferPrefix + "_" + k + "_instances", Gfx.VERTEX, b.verts, meshVersion);
			Gfx.draw(4, {
				verts: quad,
				instances: instances,
				atlas: b.atlas.texture,
				uniforms: {params: params}
			}, {
				shader: shader,
				depth: false,
				cull: Gfx.NONE,
				blend: blendMode,
				primitive: Gfx.TRIANGLE_STRIP,
				instance_count: Std.int(b.verts.length / INSTANCE_STRIDE)
			});
		}
	}
}
