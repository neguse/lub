package input;

class MockInput implements InputSource {
	final snap = new InputSnapshot();
	final script:Null<(Int) -> InputSnapshot>;
	var frame:Int = 0;

	public function new(?script:(Int) -> InputSnapshot) {
		this.script = script;
	}

	public var current(get, never):InputSnapshot;

	inline function get_current():InputSnapshot
		return snap;

	public function capture():Void {}

	public function refresh():Void {
		var s = (script == null) ? null : script(frame);
		if (s == null) {
			snap.dirX = 0;
			snap.dirY = 0;
			snap.fire = false;
			snap.slow = false;
			snap.menu = false;
			snap.cancel = false;
			snap.noGod = false;
		} else {
			snap.dirX = s.dirX;
			snap.dirY = s.dirY;
			snap.fire = s.fire;
			snap.slow = s.slow;
			snap.menu = s.menu;
			snap.cancel = s.cancel;
			snap.noGod = s.noGod;
		}
		frame = frame + 1;
	}
}
