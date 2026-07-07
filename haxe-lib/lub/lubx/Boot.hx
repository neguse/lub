package lubx;

import lub.Lub;

/** 起動定型。LUB_BACKEND env(未設定なら "native")を backend に補って
	Lub.config する。onInit 専用。opts.backend が明示されていればそちらが勝つ。 **/
class Boot {
	public static function config(?opts:Dynamic):Void {
		if (opts == null)
			opts = {};
		if (Reflect.field(opts, "backend") == null) {
			var backend:String = lua.Os.getenv("LUB_BACKEND");
			if (backend == null)
				backend = "native";
			Reflect.setField(opts, "backend", backend);
		}
		Lub.config(cast opts);
	}
}
