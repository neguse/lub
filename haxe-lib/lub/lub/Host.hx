package lub;

@:multiReturn extern class HostMsg {
	var topic:String;
	var payload:String;
}

/**
	ホストページとの汎用メッセージブリッジ (web 専用)。

	WASM を載せているページが `window.lubHost` を定義しているとき、
	topic + payload の対でメッセージを交換できる。payload はバイナリ安全な
	Lua string (コーデックは `string.pack` / `string.unpack` を想定)。
	ネットワーク等の実体はホストページ側 JS に置き、ゲームはこの
	ブリッジ越しにやり取りする。`--serve` はゲームディレクトリの
	`host.js` をページに注入する。

	native ではホストが無いので `available()` は false、`send` は捨てられ、
	`poll` は常に topic = null を返す。

	`poll` は 1 件ずつ返す。キューを空にするには topic が null になるまで
	フレーム毎に繰り返し呼ぶ。
**/
extern class Host {
	@:native("host_available") public static function available():Bool;
	@:native("host_send") public static function send(topic:String, payload:String):Void;
	@:native("host_poll") public static function poll():HostMsg;
}
