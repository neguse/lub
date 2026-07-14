package input;

import lub.Input as Keys;

class Input implements InputSource {
	final snap = new InputSnapshot();
	var up:Bool = false;
	var down:Bool = false;
	var left:Bool = false;
	var right:Bool = false;
	var fire:Bool = false;
	var slow:Bool = false;
	var noGod:Bool = false;
	var pendingFire:Bool = false;
	var pendingMenu:Bool = false;
	var pendingCancel:Bool = false;

	public function new() {}

	public var current(get, never):InputSnapshot;

	inline function get_current():InputSnapshot
		return snap;

	public function capture():Void {
		up = Keys.keyDown("up");
		down = Keys.keyDown("down");
		left = Keys.keyDown("left");
		right = Keys.keyDown("right");
		fire = Keys.keyDown("z");
		slow = Keys.keyDown("x");
		noGod = Keys.keyDown("c");
		if (Keys.keyPressed("z")) {
			pendingFire = true;
			pendingMenu = true;
		}
		if (Keys.keyPressed("escape"))
			pendingCancel = true;
	}

	public function refresh():Void {
		snap.dirX = (right ? 1 : 0) - (left ? 1 : 0);
		snap.dirY = (down ? 1 : 0) - (up ? 1 : 0);
		snap.fire = fire || pendingFire;
		snap.slow = slow;
		snap.noGod = noGod;
		snap.menu = pendingMenu;
		snap.cancel = pendingCancel;
		pendingFire = false;
		pendingMenu = false;
		pendingCancel = false;
	}
}
