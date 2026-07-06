package lub;

import lub.Mesh.MeshData;

/**
	`Font.glyph` の返すビットマップ。`px` は 1em のピクセル数 (CSS の
	font-size 相当)。`bytes` は w*h の R8 coverage を並べた Lua string
	(上から行順、`string.byte` で読める)。空グリフ (空白) は bytes 無し。
	`xoff`/`yoff` はベースライン原点から見たビットマップ左上 (y は下向き)。
**/
typedef GlyphBitmap = {
	var w:Int;
	var h:Int;
	var xoff:Int;
	var yoff:Int;
	var advance:Float;
	@:optional var bytes:String;
}

/**
	`Font.glyphMesh` の返すメッシュ。`MeshData` 規約 + `advance`。
	座標は em 単位 (1.0 = font-size 1 相当)、ベースライン原点、y は上向き、
	z=0、法線 +z。押し出せば 3D 文字になる。
**/
typedef GlyphMesh = {
	> MeshData,
	var advance:Float;
}

typedef FontMetrics = {
	var ascent:Float;
	var descent:Float;
	var line_gap:Float;
}

/**
	TTF glyph の純関数 utility。フォントの bytes (string) を毎回渡す。
	キャッシュ・atlas・レイアウトは core に入れない (lubx.Text 参照)。
	ラスタも三角形化も CPU 決定的で、全 platform 同一出力。

	対応言語の境界は「cmap 表引きで正しく出るもの」(FIGS + CCJK 横書きまで)。
	シェーピングが要る文字体系はグリフが nil になるか壊れた並びになるので
	使わないこと (docs/roadmap.md)。カバー外の codepoint は nil を返すので、
	フォールバックは呼び出し側で別フォントに聞き直す。
**/
extern class Font {
	/** ascent/descent/line_gap を em 単位で返す (descent は負)。 **/
	@:native("font_metrics") public static function metrics(ttf:String):FontMetrics;

	/** グリフを px サイズでラスタライズ。フォントに無い codepoint は null。 **/
	@:native("font_glyph") public static function glyph(ttf:String, codepoint:Int, px:Float):Null<GlyphBitmap>;

	/**
		グリフ輪郭を三角形化したメッシュ (em 単位、y-up)。`tolerance` は
		曲線平坦化の最大誤差 (em、既定 0.002)。空白は vert_count=0 の
		空メッシュ、フォントに無い codepoint は null。
	**/
	@:native("font_glyph_mesh") public static function glyphMesh(ttf:String, codepoint:Int, ?tolerance:Float):Null<GlyphMesh>;

	/** ペアカーニング (em 単位、無ければ 0)。 **/
	@:native("font_kern") public static function kern(ttf:String, cp1:Int, cp2:Int):Float;
}
