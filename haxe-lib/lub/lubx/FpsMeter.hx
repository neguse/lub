package lubx;

import lub.Sys;

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
