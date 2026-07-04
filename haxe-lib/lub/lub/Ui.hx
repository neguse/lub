package lub;

/** `Ui.colorEdit3` の multi return。 **/
@:multiReturn extern class UiColor {
	var r:Float;
	var g:Float;
	var b:Float;
}

/**
	Dear ImGui の debug UI(厳選サブセット)。immediate mode:
	毎フレーム widget を宣言し、返り値が新しい値(状態は呼び側が持つ)。

	`render()` は `Gfx.beginPass` 〜 `endPass` の間で 1 回呼ぶ。
	widget 宣言は onFrame 中ならどこでもよい。位置づけは開発・チューニング
	用で、ゲーム本編の UI は従来通り自前描画で作る。

	```haxe
	if (Ui.begin("tuning")) {
		radius = Ui.slider("radius", radius, 0.1, 2.0);
		if (Ui.button("reset")) radius = 1.0;
	}
	Ui.end();
	// ... beginPass 中に
	Ui.render();
	```
**/
extern class Ui {
	/** draw list を発行する。`beginPass` 中に呼ぶこと。 **/
	@:native("ui_render") public static function render():Void;

	/** window を開く。返り値が false でも `end()` は必ず呼ぶ。 **/
	@:native("ui_begin") public static function begin(title:String):Bool;

	@:native("ui_end") public static function end():Void;
	@:native("ui_text") public static function text(s:String):Void;
	@:native("ui_button") public static function button(label:String):Bool;
	@:native("ui_checkbox") public static function checkbox(label:String, v:Bool):Bool;
	@:native("ui_slider_float") public static function slider(label:String, v:Float, min:Float, max:Float):Float;
	@:native("ui_slider_int") public static function sliderInt(label:String, v:Int, min:Int, max:Int):Int;
	@:native("ui_drag_float") public static function drag(label:String, v:Float, ?speed:Float, ?min:Float, ?max:Float):Float;
	@:native("ui_color_edit3") public static function colorEdit3(label:String, r:Float, g:Float, b:Float):UiColor;
	@:native("ui_separator") public static function separator():Void;
	@:native("ui_same_line") public static function sameLine():Void;

	/** 階層ノード。true が返ったら子を描いて `treePop()` する。 **/
	@:native("ui_tree_node") public static function treeNode(label:String, ?defaultOpen:Bool):Bool;

	@:native("ui_tree_pop") public static function treePop():Void;

	/** 次の window の初期配置(初回のみ。ユーザのドラッグは活きる)。 **/
	@:native("ui_set_next_window") public static function setNextWindow(x:Float, y:Float, w:Float, h:Float):Void;

	/** UI がマウスを取っている間 true。ゲーム入力の無視判定に。 **/
	@:native("ui_want_capture_mouse") public static function wantCaptureMouse():Bool;
}
