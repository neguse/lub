package lubx;

import lub.Font;
import lub.Gfx;

@:native("utf8")
private extern class NativeUtf8 {
	static function codepoint(s:String, i:Int):Int;
	static function offset(s:String, n:Int, i:Int):Null<Int>;
}

@:native("string")
private extern class NativeString {
	static function len(s:String):Int;
}

private typedef GlyphEntry = {
	vb:Dynamic,
	ib:Dynamic,
	count:Int,
	advance:Float,
	cx:Float,
	cy:Float
};

/** メッシュグリフ描画 (大サイズレジーム)。TTF 輪郭を三角形化して描くので
	拡大しても輪郭が崩れない。小サイズ本文は lubx.Text (bitmap) を使うこと。
	座標は論理解像度 px、y は下向き、(x, y) はベースライン。 **/
class MeshText {
	static var VS:String = "struct Uniforms {\n"
		+ "  float4\n"
		+ "      psr; // x, y (screen px), scale (px per em), rotation (rad, CCW in y-up)\n"
		+ "  float4 tint;\n"
		+ "  float4 screen; // logical w, h\n"
		+ "  float4 center; // rotation/placement center in em (glyph bbox center)\n"
		+ "};\n"
		+ "ConstantBuffer<Uniforms> u;\n"
		+ "\n"
		+ "struct VSIn {\n"
		+ "  float2 pos : POSITION; // em units, y-up, baseline origin\n"
		+ "};\n"
		+ "\n"
		+ "struct VSOut {\n"
		+ "  float4 color : COLOR;\n"
		+ "  float4 pos : SV_Position;\n"
		+ "};\n"
		+ "\n"
		+ "[shader(\"vertex\")] VSOut vs_main(VSIn i) {\n"
		+ "  VSOut o;\n"
		+ "  float c = cos(u.psr.w);\n"
		+ "  float s = sin(u.psr.w);\n"
		+ "  float2 l = (i.pos - u.center.xy) * u.psr.z;\n"
		+ "  float2 r = float2(l.x * c - l.y * s, l.x * s + l.y * c);\n"
		+ "  float2 p = float2(u.psr.x + r.x, u.psr.y - r.y); // y-up -> screen y-down\n"
		+ "  o.pos = float4(p.x / u.screen.x * 2.0 - 1.0, 1.0 - p.y / u.screen.y * 2.0,\n"
		+ "                 0.0, 1.0);\n"
		+ "  o.color = u.tint;\n"
		+ "  return o;\n"
		+ "}\n";

	static var FS:String = "struct FSIn {\n"
		+ "  float4 color : COLOR;\n"
		+ "};\n"
		+ "\n"
		+ "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target { return i.color; }\n";

	final key:String;
	final ttf:String;
	final version:Int;
	final logicalW:Int;
	final logicalH:Int;
	final glyphs = new Map<Int, GlyphEntry>();
	var shader:Dynamic = null;

	public function new(key:String, ttf:String, version:Int, logicalW:Int, logicalH:Int) {
		this.key = key;
		this.ttf = ttf;
		this.version = version;
		this.logicalW = logicalW;
		this.logicalH = logicalH;
	}

	function ensure():Bool {
		shader = Gfx.useShader(key + "_shader", VS, FS, 1);
		return shader != null;
	}

	function glyphFor(cp:Int):GlyphEntry {
		var e = glyphs.get(cp);
		if (e != null)
			return e;
		var gm = Font.glyphMesh(ttf, cp);
		if (gm == null)
			return null;
		if (gm.vert_count == 0) {
			// 空グリフ (スペース等) も advance を持つのでキャッシュする
			e = {
				vb: null,
				ib: null,
				count: 0,
				advance: gm.advance,
				cx: 0.0,
				cy: 0.0
			};
			glyphs.set(cp, e);
			return e;
		}
		var verts = new Array<Float>();
		var minX = 1e9, minY = 1e9, maxX = -1e9, maxY = -1e9;
		for (i in 0...gm.vert_count) {
			var x:Float = gm.positions[i * 3 + 1];
			var y:Float = gm.positions[i * 3 + 2];
			verts.push(x);
			verts.push(y);
			if (x < minX)
				minX = x;
			if (x > maxX)
				maxX = x;
			if (y < minY)
				minY = y;
			if (y > maxY)
				maxY = y;
		}
		var idx = new Array<Float>();
		for (i in 0...gm.index_count)
			idx.push(gm.indices[i + 1]);
		e = {
			vb: Gfx.useBuffer(key + "_v:" + cp, Gfx.VERTEX, verts, version),
			ib: Gfx.useBuffer(key + "_i:" + cp, Gfx.INDEX, idx, version),
			count: gm.index_count,
			advance: gm.advance,
			cx: (minX + maxX) * 0.5,
			cy: (minY + maxY) * 0.5,
		};
		glyphs.set(cp, e);
		return e;
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

	/** グリフ1つ。(x, y) は centered=false ならベースライン原点、true なら bbox 中心を (x,y) に置く。
		size は px/em、angle は CCW ラジアン。 **/
	public function glyph(cp:Int, x:Float, y:Float, size:Float, angle:Float = 0.0, ?tint:Color, centered:Bool = false):Void {
		if (!ensure())
			return;
		var e = glyphFor(cp);
		if (e == null || e.count == 0)
			return;
		var c = colorOrWhite(tint);
		Gfx.draw(e.count, {
			verts: e.vb,
			indices: e.ib,
			uniforms: {
				psr: lua.Table.fromArray([x, y, size, angle]),
				tint: lua.Table.fromArray([c.r, c.g, c.b, c.a]),
				screen: lua.Table.fromArray([(logicalW : Float), (logicalH : Float), 0.0, 0.0]),
				center: lua.Table.fromArray(centered ? [e.cx, e.cy, 0.0, 0.0] : [0.0, 0.0, 0.0, 0.0]),
			},
		}, {
			shader: shader,
			depth: false,
			cull: Gfx.NONE,
			blend: Gfx.ALPHA,
		});
	}

	/** 文字列の先頭グリフ1つを描く。glyph() の String 版。 **/
	public function char(s:String, x:Float, y:Float, size:Float, angle:Float = 0.0, ?tint:Color, centered:Bool = false):Void {
		glyph(NativeUtf8.codepoint(s, 1), x, y, size, angle, tint, centered);
	}

	/** 1行をベースライン左端から。 **/
	public function text(s:String, x:Float, baselineY:Float, size:Float, ?tint:Color):Void {
		var pen = x;
		var n = NativeString.len(s);
		var i:Null<Int> = 1;
		while (i != null) {
			var pos:Int = i;
			if (pos > n)
				break;
			var cp = NativeUtf8.codepoint(s, pos);
			var e = glyphFor(cp);
			if (e != null) {
				glyph(cp, pen, baselineY, size, 0.0, tint, false);
				pen += e.advance * size;
			}
			i = NativeUtf8.offset(s, 2, pos);
		}
	}

	/** 1行を中央揃えで (cx は中心)。 **/
	public function textCentered(s:String, cx:Float, baselineY:Float, size:Float, ?tint:Color):Void {
		text(s, cx - width(s, size) * 0.5, baselineY, size, tint);
	}

	/** 1行の幅 (px)。advance の合計 × size。 **/
	public function width(s:String, size:Float):Float {
		var sum = 0.0;
		var n = NativeString.len(s);
		var i:Null<Int> = 1;
		while (i != null) {
			var pos:Int = i;
			if (pos > n)
				break;
			var e = glyphFor(NativeUtf8.codepoint(s, pos));
			if (e != null)
				sum += e.advance;
			i = NativeUtf8.offset(s, 2, pos);
		}
		return sum * size;
	}
}
