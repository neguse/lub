package lubx;

/** RGBA カラー。各成分 0..1。構造体リテラル {r, g, b, a} からの暗黙変換可。 **/
@:forward
abstract Color({
	r:Float,
	g:Float,
	b:Float,
	a:Float
}) from {
	r:Float,
	g:Float,
	b:Float,
	a:Float
} to {
	r:Float,
	g:Float,
	b:Float,
	a:Float
	} {
	inline function new(r:Float, g:Float, b:Float, a:Float) {
		this = {
			r: r,
			g: g,
			b: b,
			a: a
		};
	}

	/** 成分指定 (a 省略で 1.0)。 **/
	public static inline function rgb(r:Float, g:Float, b:Float, a:Float = 1.0):Color {
		return new Color(r, g, b, a);
	}

	/** 0xRRGGBB (a 省略で 1.0)。例: Color.hex(0xE85C5C)。 **/
	public static function hex(rgb:Int, a:Float = 1.0):Color {
		return new Color(((rgb >> 16) & 0xff) / 255.0, ((rgb >> 8) & 0xff) / 255.0, (rgb & 0xff) / 255.0, a);
	}
}
