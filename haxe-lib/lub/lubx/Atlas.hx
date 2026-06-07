package lubx;

import lub.Gfx;

class Atlas {
	public var texture:Dynamic = null;
	public var w:Int = 0;
	public var h:Int = 0;
	public final key:String;

	var path:String = null;
	var pixels:Array<Int> = null;
	var pixelTable:Dynamic = null;
	var format:Int = 0;
	var version:Int = 1;
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

	public static function fromPixels(key:String, w:Int, h:Int, pixels:Array<Int>, version:Int, ?opts:Dynamic):Atlas {
		var a = new Atlas(key);
		a.w = w;
		a.h = h;
		a.pixels = pixels;
		a.version = version;
		a.format = Gfx.RGBA8;
		a.opts = opts;
		return a;
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
			texture = Gfx.useTexture(key, w, h, format, pixelTable, version, textureOpts());
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
