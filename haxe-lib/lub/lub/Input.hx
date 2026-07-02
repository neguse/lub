package lub;

/**
	`Input.key*` に渡すキー名。下記の定数のほか、1 文字の
	`"a".."z"` / `"0".."9"` がそのまま使える (大文字小文字は無視)。
	未対応の名前は常に false を返す。
**/
enum abstract Key(String) from String to String {
	var Space = "space";
	var Enter = "enter";
	var Escape = "escape";
	var Tab = "tab";
	var Backspace = "backspace";
	var Left = "left";
	var Right = "right";
	var Up = "up";
	var Down = "down";
}

/** マウスボタン。SDL 準拠で 1 始まり。0 以下はランタイムエラーになる。 **/
enum abstract MouseButton(Int) from Int to Int {
	var Left = 1;
	var Middle = 2;
	var Right = 3;
}

@:multiReturn extern class MouseDelta {
	var dx:Float;
	var dy:Float;
}

@:multiReturn extern class MousePos {
	var x:Float;
	var y:Float;
}

/**
	フレームラッチ付きポーリング入力。

	- `*Down`: 現在押されているか (現在状態)。
	- `*Pressed` / `*Released`: このフレーム中に押された / 離されたか。
	  ランタイムがフレーム間の SDL イベントからラッチするので、
	  1 フレームより短い押下も取りこぼさない。onFrame 終了時にクリアされる。

	タッチ (web) は SDL の touch→mouse 合成により primary の指が
	マウス左ボタンとして観測される。
**/
extern class Input {
	@:native("key_down") public static function keyDown(key:Key):Bool;
	@:native("key_pressed") public static function keyPressed(key:Key):Bool;
	@:native("key_released") public static function keyReleased(key:Key):Bool;

	@:native("mouse_down") public static function mouseDown(?button:MouseButton):Bool;
	@:native("mouse_pressed") public static function mousePressed(?button:MouseButton):Bool;
	@:native("mouse_released") public static function mouseReleased(?button:MouseButton):Bool;

	/** カーソルの絶対座標 (window px)。 **/
	@:native("mouse_pos") public static function mousePos():MousePos;

	/** このフレームの相対移動量 (window px) の合計。フレーム内で何度呼んでも同じ値。 **/
	@:native("mouse_delta") public static function mouseDelta():MouseDelta;
}
