package input;

import lub.Input as Keys;

class Input implements InputSource {
	final snap = new InputSnapshot();
	var prevFire:Bool = false;
	var prevCancel:Bool = false;

	public function new() {}

	public var current(get, never):InputSnapshot;

	inline function get_current():InputSnapshot
		return snap;

	public function refresh():Void {
		var up = Keys.keyDown("up");
		var down = Keys.keyDown("down");
		var left = Keys.keyDown("left");
		var right = Keys.keyDown("right");
		var z = Keys.keyDown("z");
		var x = Keys.keyDown("x");
		var c = Keys.keyDown("c");
		var esc = Keys.keyDown("escape");

		snap.dirX = (right ? 1 : 0) - (left ? 1 : 0);
		snap.dirY = (down ? 1 : 0) - (up ? 1 : 0);
		snap.fire = z;
		snap.slow = x;
		snap.noGod = c;
		snap.menu = z && !prevFire;
		snap.cancel = esc && !prevCancel;

		prevFire = z;
		prevCancel = esc;
	}
}
