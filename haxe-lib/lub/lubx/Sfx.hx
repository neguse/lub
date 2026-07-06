package lubx;

import lub.Audio;

/** 効果音の即席合成。同じパラメータからは常に同じ波形を生成する (決定的) ので、
	Audio.pcm の内容 dedupe と合わせて hot reload しても snd handle が安定する。
	結果は内部 cache するので毎フレーム宣言的に呼んでよい。 **/
class Sfx {
	public static inline var RATE = 44100;

	static var cache = new Map<String, Int>();

	/** 矩形波 blip。freq0→freq1 へスイープしつつ指数減衰 (exp(-5u))。snd handle を返す。 **/
	public static function blip(freq0:Float, freq1:Float, dur:Float, vol:Float):Int {
		var key = "blip:" + freq0 + ":" + freq1 + ":" + dur + ":" + vol;
		var snd = cache.get(key);
		if (snd != null)
			return snd;
		var n = Std.int(dur * RATE);
		var out = lua.Table.create();
		var phase = 0.0;
		for (i in 0...n) {
			var u = i / n;
			var freq = freq0 + (freq1 - freq0) * u;
			phase += freq / RATE;
			var env = Math.exp(-5.0 * u);
			out[i + 1] = ((phase % 1.0) < 0.5 ? 1.0 : -1.0) * env * vol;
		}
		snd = Audio.pcm(out, 1, RATE);
		cache.set(key, snd);
		return snd;
	}

	/** ノイズバースト。指数減衰 (exp(-4u))、16 sample ごとにホールド更新。snd handle を返す。 **/
	public static function noise(dur:Float, vol:Float, seed:Int = 0x12345678):Int {
		var key = "noise:" + dur + ":" + vol + ":" + seed;
		var snd = cache.get(key);
		if (snd != null)
			return snd;
		var n = Std.int(dur * RATE);
		var out = lua.Table.create();
		var r = new Rand(seed);
		var hold = 0.0;
		for (i in 0...n) {
			if (i % 16 == 0)
				hold = r.float() * 2.0 - 1.0;
			var u = i / n;
			out[i + 1] = hold * Math.exp(-4.0 * u) * vol;
		}
		snd = Audio.pcm(out, 1, RATE);
		cache.set(key, snd);
		return snd;
	}
}
