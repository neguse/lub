package input;

interface InputSource {
	public function capture():Void; // render ごとに実入力と edge を保存
	public function refresh():Void; // fixed tick ごとに snapshot を更新
	public var current(get, never):InputSnapshot;
}
