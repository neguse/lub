package lubx;

import lub.Gfx;
import lub.Io;

/** アセット読み込みの定型を1行にする。ready まで null を返す宣言型
	(毎フレーム呼んで null の間は描画をスキップする)。 **/
class Assets {
	/** vs/fs の Slang を読んで useShader。どちらか未 ready なら null。 **/
	public static function shader(key:String, vsPath:String, fsPath:String):Dynamic {
		var vsResult = Io.loadText(vsPath);
		var fsResult = Io.loadText(fsPath);
		if (vsResult.text == null || fsResult.text == null)
			return null;
		return Gfx.useShader(key, vsResult.text, fsResult.text, vsResult.version * 31 + fsResult.version);
	}

	/** loadFloats + useBuffer。data 未 ready なら null。usage は Gfx.VERTEX / Gfx.INDEX。 **/
	public static function floats(key:String, usage:Int, path:String):Dynamic {
		var r = Io.loadFloats(path);
		if (r.data == null)
			return null;
		return Gfx.useBuffer(key, usage, r.data, r.version);
	}
}
