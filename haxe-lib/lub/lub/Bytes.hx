package lub;

/**
	ランタイム側が所有するバイト列への view (`Png.load` の結果や readback、
	`Audio.decode` の転送用)。返された frame の終わりまで有効で、古い view を
	API に渡すと error になる。Haxe 側から中身は読めず、`Gfx.useTexture` の
	`px` や `Png.write` にそのまま渡す。frame を跨いで持ちたい内容は毎フレーム
	取り直すか自分の memory に写す。
**/
extern class Bytes {
	public var length(default, null):Int;
}
