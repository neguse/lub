package lubx;

/** 決定的な xorshift32 乱数。固定シードで hot reload / headless 検証でも
	再現可能 (Math.random は reload のたびに列が変わるので使わない)。 **/
class Rand {
	var state:Int;

	public function new(seed:Int = 0x12345678) {
		state = (seed == 0) ? 0x12345678 : seed;
	}

	/** [0, 1) の一様乱数。 **/
	public function float():Float {
		state ^= state << 13;
		state ^= state >>> 17;
		state ^= state << 5;
		return (state & 0xffff) / 65536.0;
	}

	/** [0, n) の整数。 **/
	public function int(n:Int):Int {
		return Std.int(float() * n);
	}

	/** [min, max) の一様乱数。 **/
	public function range(min:Float, max:Float):Float {
		return min + float() * (max - min);
	}
}
