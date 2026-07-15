package lubx;

import lub.Gfx;

/**
	`SpriteBatch` 用のテクスチャアトラス。PNG (`fromPng`) か生ピクセル
	(`fromPixels`) から作り、`ensure()` が true を返したフレームから
	描画に使える (web ではロード完了まで false)。
**/
class Atlas {
	public var texture:Dynamic = null;
	public var w:Int = 0;
	public var h:Int = 0;
	public final key:String;

	var path:String = null;
	var pixels:Array<Int> = null;
	var pixelTable:Dynamic = null;
	var format:Int = 0;
	var version:Null<Int> = null;
	var dirty:Bool = true;
	var opts:Dynamic = null;

	public function new(key:String) {
		this.key = key;
	}

	public static function fromPng(key:String, path:String, ?opts:Dynamic):Atlas {
		var a = new Atlas(key);
		a.path = path;
		a.opts = opts;
		return a;
	}

	/**
		`version` は内容から導ける同一性の値があるときだけ渡す(不変内容なら
		定数)。省略すると「変更宣言 + ref.version 再主張」を Atlas が内部で
		管理する。
	**/
	public static function fromPixels(key:String, w:Int, h:Int, pixels:Array<Int>, ?version:Int, ?opts:Dynamic):Atlas {
		var a = new Atlas(key);
		a.w = w;
		a.h = h;
		a.pixels = pixels;
		a.version = version;
		a.format = Gfx.RGBA8;
		a.opts = opts;
		return a;
	}

	/**
		動的 atlas 用: ピクセル配列を差し替えて変更を宣言する。次の
		`ensure()` で再アップロードされる (`lubx.Text` の glyph 追加が使う)。
	**/
	public function updatePixels(pixels:Array<Int>) {
		this.pixels = pixels;
		this.dirty = true;
		this.pixelTable = null;
	}

	function textureOpts():Dynamic {
		if (opts != null)
			return opts;
		return {filter: Gfx.LINEAR, wrap: Gfx.CLAMP};
	}

	public function ensure():Bool {
		if (pixels != null) {
			if (pixelTable == null)
				pixelTable = lua.Table.fromArray(pixels);
			if (version != null) {
				// caller 提供の同一性の値(定数など)
				texture = Gfx.useTexture(key, w, h, format, pixelTable, version, textureOpts());
			} else if (dirty || texture == null) {
				// 変更宣言: runtime が実効 version を発行して必ず upload
				texture = Gfx.useTexture(key, w, h, format, pixelTable, null, textureOpts());
				dirty = false;
			} else {
				// 再主張: 前回の実効 version で upload を skip
				texture = Gfx.useTexture(key, w, h, format, pixelTable, texture.version, textureOpts());
			}
			return true;
		}

		if (path == null || path == "")
			return texture != null;

		var r = Png.load(path);
		if (r.bytes == null)
			return false;
		w = r.width;
		h = r.height;
		texture = Gfx.useTexture(key, r.width, r.height, r.format, r.bytes, r.version, textureOpts());
		return true;
	}
}
