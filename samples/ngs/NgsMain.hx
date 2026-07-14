import game.Game;

class NgsMain {
	public static function main() {}

	public static function onInit() {
		Game.init();
	}

	public static function onFrame(dt:Float) {
		Game.frame(dt);
	}
}
