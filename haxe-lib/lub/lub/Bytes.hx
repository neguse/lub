package lub;

/**
	ランタイム側が所有するバイト列への不透明ハンドル
	(`Png.load` の結果や readback の転送用)。Haxe 側から中身は読めず、
	`Gfx.useTexture` の `px` や `Png.write` にそのまま渡す。
**/
extern class Bytes {
	public var length(default, null):Int;
}
