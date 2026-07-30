package lubx;

import lub.Lub;

/** 起動定型。LUB_BACKEND env が設定されていれば backend に補って
	Lub.config する。未設定ならランタイムのプラットフォーム既定に任せる。
	onInit 専用。opts.backend が明示されていればそちらが勝つ。 **/
class Boot {
	public static function config(?opts:Dynamic):Void {
		if (opts == null)
			opts = {};
		if (Reflect.field(opts, "backend") == null) {
			var backend:String = lua.Os.getenv("LUB_BACKEND");
			if (backend != null)
				Reflect.setField(opts, "backend", backend);
		}
		Lub.config(cast opts);
	}
}
