import lubx.Boot;
import lub.Gfx;
import lua.Lua;

class Hello00 {
	public static function main() {}

	public static function onInit() {
		Lua.print("[lua] onInit");
		Boot.config({});
		Lua.print("config called");
		Lua.print("VERTEX=", Gfx.VERTEX, "RGBA8=", Gfx.RGBA8, "CLEAR=", Gfx.CLEAR);
		var mt:Dynamic = Gfx.mainTex;
		if (mt != null && mt.__lub_kind == "main_tex") {
			Lua.print("main_tex is registered");
		}
	}

	public static function onEvent(e:Dynamic) {}

	public static function onFrame() {
		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])
		});
		Gfx.endPass();
	}

	public static function onQuit() {
		Lua.print("[lua] onQuit");
	}
}
