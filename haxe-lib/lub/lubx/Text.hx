package lubx;

import lub.Font;

@:native("utf8")
private extern class NativeUtf8 {
	static function codepoint(s:String, i:Int):Int;
	static function offset(s:String, n:Int, i:Int):Null<Int>;
}

@:native("string")
private extern class NativeString {
	static function byte(s:String, i:Int):Int;
	static function len(s:String):Int;
}

private typedef TextGlyph = {u:Int, v:Int, w:Int, h:Int, xoff:Int, yoff:Int, advance:Float};

/**
	固定サイズの動的 glyph atlas + 1行テキスト描画。`Font.glyph` を
	オンデマンドに呼び、使ったグリフだけを atlas (RGBA、白 + coverage α)
	に詰めて `SpriteBatch` で描く。フォントに無い codepoint は黙って
	スキップされる (フォールバックチェーンは呼び出し側で Text を重ねる)。

	座標は SpriteBatch と同じ論理 px・左上原点で、`draw` の y は
	ベースライン位置。折返し・禁則はまだ無い (docs/roadmap.md)。
**/
class Text {
	/** 1em のピクセル数 (コンストラクタ指定)。 **/
	public final px:Float;

	/** ベースラインから上端まで (px、正)。 **/
	public var ascent(default, null):Float;

	/** ベースラインから下端まで (px、負)。 **/
	public var descent(default, null):Float;

	/** 行送り (px)。 **/
	public var lineHeight(default, null):Float;

	final ttf:String;
	final atlas:Atlas;
	final atlasW:Int;
	final atlasH:Int;
	final pixels:Array<Int>;
	final glyphs = new Map<Int, TextGlyph>();
	final missing = new Map<Int, Bool>();
	var penX:Int = 1;
	var penY:Int = 1;
	var rowH:Int = 0;

	public function new(key:String, ttf:String, px:Float, atlasSize:Int = 256) {
		this.ttf = ttf;
		this.px = px;
		atlasW = atlasSize;
		atlasH = atlasSize;
		pixels = [for (_ in 0...atlasW * atlasH * 4) 0];
		var m = Font.metrics(ttf);
		ascent = m.ascent * px;
		descent = m.descent * px;
		lineHeight = (m.ascent - m.descent + m.line_gap) * px;
		atlas = Atlas.fromPixels(key, atlasW, atlasH, pixels);
	}

	static function eachCodepoint(s:String, f:Int->Void) {
		var n = NativeString.len(s);
		var i:Null<Int> = 1;
		while (i != null) {
			var pos:Int = i;
			if (pos > n)
				break;
			f(NativeUtf8.codepoint(s, pos));
			i = NativeUtf8.offset(s, 2, pos);
		}
	}

	function ensureGlyph(cp:Int):Null<TextGlyph> {
		var g = glyphs.get(cp);
		if (g != null)
			return g;
		if (missing.exists(cp))
			return null;
		var gb = Font.glyph(ttf, cp, px);
		if (gb == null) {
			missing.set(cp, true);
			return null;
		}
		var u = 0, v = 0;
		if (gb.bytes != null && gb.w > 0 && gb.h > 0) {
			if (penX + gb.w + 1 > atlasW) {
				penX = 1;
				penY += rowH + 1;
				rowH = 0;
			}
			if (penY + gb.h + 1 > atlasH) {
				trace("lubx.Text: atlas full, glyph dropped: " + cp);
				missing.set(cp, true);
				return null;
			}
			u = penX;
			v = penY;
			var src = 1;
			for (row in 0...gb.h) {
				var dst = ((v + row) * atlasW + u) * 4;
				for (_ in 0...gb.w) {
					var a = NativeString.byte(gb.bytes, src);
					src++;
					pixels[dst] = 255;
					pixels[dst + 1] = 255;
					pixels[dst + 2] = 255;
					pixels[dst + 3] = a;
					dst += 4;
				}
			}
			penX += gb.w + 1;
			if (gb.h > rowH)
				rowH = gb.h;
			atlas.updatePixels(pixels);
		}
		g = {
			u: u,
			v: v,
			w: gb.w,
			h: gb.h,
			xoff: gb.xoff,
			yoff: gb.yoff,
			advance: gb.advance
		};
		glyphs.set(cp, g);
		return g;
	}

	/** 1行の描画幅 (px)。 **/
	public function width(s:String, scale:Float = 1.0):Float {
		var sum = 0.0;
		var prev = -1;
		eachCodepoint(s, cp -> {
			var g = ensureGlyph(cp);
			if (g == null)
				return;
			if (prev >= 0)
				sum += Font.kern(ttf, prev, cp) * px;
			sum += g.advance;
			prev = cp;
		});
		return sum * scale;
	}

	/** 1行描く。(x, y) はベースライン左端 (論理 px、左上原点)。 **/
	public function draw(batch:SpriteBatch, s:String, x:Float, y:Float, ?tint:Color, scale:Float = 1.0) {
		var pen = x;
		var prev = -1;
		eachCodepoint(s, cp -> {
			var g = ensureGlyph(cp);
			if (g == null)
				return;
			if (prev >= 0)
				pen += Font.kern(ttf, prev, cp) * px * scale;
			if (g.w > 0)
				batch.quad(atlas, {
					x: g.u,
					y: g.v,
					w: g.w,
					h: g.h
				}, pen + g.xoff * scale, y + g.yoff * scale, g.w * scale, g.h * scale, tint);
			pen += g.advance * scale;
			prev = cp;
		});
	}
}
