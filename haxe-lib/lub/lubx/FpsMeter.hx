package lubx;

import lub.Sys;

/**
	`Sys.actualFps` の値を保持するだけの小物。計測が入る前 (起動直後) は
	`initialFps` を返し続ける。毎フレーム `tick()` を呼ぶ。
**/
class FpsMeter {
	public var fps(default, null):Float;

	public function new(initialFps:Float = 60.0) {
		this.fps = initialFps;
	}

	public function tick():Float {
		var measured = Sys.actualFps();
		if (measured > 0)
			fps = measured;
		return fps;
	}
}
