package render;

class Font {
	public final atlas:Atlas;

	public static inline var GW:Int = 8;
	public static inline var GH:Int = 8;
	static inline var COLS:Int = 16;

	public function new(atlas:Atlas) {
		this.atlas = atlas;
	}

	// ASCII 0x20..0x7F を 16 列グリッドで引く。それ以外は空白扱い。
	public function drawString(dl:DrawList, x:Int, y:Int, s:String, ?tint:Color) {
		for (i in 0...s.length) {
			var ch = s.charCodeAt(i);
			if (ch < 0x20 || ch > 0x7f)
				continue;
			var gi = ch - 0x20;
			var col = gi % COLS;
			var row = Std.int(gi / COLS);
			var src:Rect = {
				x: col * GW,
				y: row * GH,
				w: GW,
				h: GH
			};
			dl.sprite(atlas, src, x + i * GW, y, tint);
		}
	}

	// width 桁の先頭ゼロ詰め整数描画。負数は符号を保ったまま数値部だけ詰める。
	public function drawInt(dl:DrawList, x:Int, y:Int, n:Int, width:Int, ?tint:Color) {
		var neg = n < 0;
		var digits = Std.string(neg ? -n : n);
		while (digits.length < width)
			digits = "0" + digits;
		drawString(dl, x, y, neg ? "-" + digits : digits, tint);
	}
}
