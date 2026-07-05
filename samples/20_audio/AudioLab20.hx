import lub.Audio;
import lub.Gfx;
import lub.Lub;
import lub.Ui;

// サウンドラボ: ImGui で波形パラメータを調整しながら音を作る。
// - 波形はコード合成 (raw PCM → Audio.pcm)。パラメータが変わったら作り直し、
//   古い snd は少し待ってから Audio.free する (fade 中の voice を殺さないため)。
// - oneshot は play ボタン、継続音は "loop voice" を ON にすると毎フレーム
//   宣言される。pitch は負値 (逆再生) や 0 (停止) も試せる。
class AudioLab20 {
	static inline var RATE = 44100;

	// --- 合成パラメータ (ImGui が直接いじる状態) ---------------------------
	static var wave = 0; // 0=square 1=saw 2=triangle 3=sine 4=noise
	static var freq0 = 440.0;
	static var freq1 = 440.0;
	static var duration = 0.25;
	static var decay = 5.0;
	static var duty = 0.5;

	// --- 再生パラメータ ------------------------------------------------------
	static var volume = 0.5;
	static var pan = 0.0;
	static var playOnChange = true;
	static var voiceOn = false;
	static var voicePitch = 1.0;
	static var master = 1.0;

	static var snd = 0;
	static var lastKey = "";
	// 差し替えた snd の遅延 free (fade out が終わる猶予を置く)
	static var retired:Array<{snd:Int, frames:Int}> = [];

	static final waveNames = ["square", "saw", "triangle", "sine", "noise"];

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	static function synth():lua.Table<Int, Float> {
		var n = Std.int(duration * RATE);
		var out = lua.Table.create();
		var phase = 0.0;
		var seed = 0x2F6E2B1;
		var hold = 0.0;
		for (i in 0...n) {
			var u = i / n;
			var freq = freq0 + (freq1 - freq0) * u;
			phase += freq / RATE;
			if (phase >= 1.0) {
				phase -= 1.0;
				// noise は周期ごとに LFSR を進める sample & hold (pitched noise)
				seed ^= seed << 13;
				seed ^= seed >>> 17;
				seed ^= seed << 5;
				hold = (seed & 0xffff) / 32768.0 - 1.0;
			}
			var s = switch (wave) {
				case 0: phase < duty ? 1.0 : -1.0;
				case 1: phase * 2.0 - 1.0;
				case 2: phase < 0.5 ? phase * 4.0 - 1.0 : 3.0 - phase * 4.0;
				case 3: Math.sin(phase * 2.0 * Math.PI);
				case _: hold;
			}
			out[i + 1] = s * Math.exp(-decay * u);
		}
		return out;
	}

	// パラメータが変わっていたら snd を作り直す。内容 dedupe があるので
	// 同じパラメータに戻せば同じ handle が返り、free 済みなら作り直される。
	static function ensureSnd():Bool {
		var key = wave + ":" + freq0 + ":" + freq1 + ":" + duration + ":" + decay + ":" + duty;
		if (key == lastKey && snd != 0)
			return false;
		if (snd != 0)
			retired.push({snd: snd, frames: 30});
		snd = Audio.pcm(synth(), 1, RATE);
		lastKey = key;
		return true;
	}

	static function sweepRetired() {
		var i = retired.length - 1;
		while (i >= 0) {
			retired[i].frames--;
			if (retired[i].frames <= 0) {
				// 差し替え後も同じ内容で作り直されて現役に戻っている
				// (dedupe で同じ handle) 場合は free しない
				if (retired[i].snd != snd)
					Audio.free(retired[i].snd);
				retired.splice(i, 1);
			}
			i--;
		}
	}

	public static function onFrame(dt:Float) {
		if (Ui.begin("sound lab")) {
			Ui.text("waveform: " + waveNames[wave]);
			wave = Ui.sliderInt("wave", wave, 0, 4);
			freq0 = Ui.slider("freq start (Hz)", freq0, 40, 2000);
			freq1 = Ui.slider("freq end (Hz)", freq1, 40, 2000);
			duration = Ui.slider("duration (s)", duration, 0.02, 1.0);
			decay = Ui.slider("decay", decay, 0.0, 12.0);
			if (wave == 0)
				duty = Ui.slider("duty", duty, 0.05, 0.95);
			Ui.separator();

			volume = Ui.slider("volume", volume, 0.0, 1.0);
			pan = Ui.slider("pan", pan, -1.0, 1.0);
			var changed = ensureSnd();
			var hit = Ui.button("play");
			Ui.sameLine();
			playOnChange = Ui.checkbox("play on change", playOnChange);
			if (hit || (changed && playOnChange && !voiceOn))
				Audio.play(snd, {volume: volume, pan: pan});
			Ui.separator();

			// 継続音: ON の間だけ毎フレーム宣言する。OFF で fade out。
			// pitch < 0 は逆再生、0 で停止 (ターンテーブルのつもりで)。
			voiceOn = Ui.checkbox("loop voice", voiceOn);
			voicePitch = Ui.slider("voice pitch", voicePitch, -2.0, 2.0);
			Ui.separator();

			master = Ui.slider("master", master, 0.0, 1.0);
			Audio.masterVolume(master);
			var info = Audio.info();
			Ui.text("voices " + info.voices + " / snds " + info.snds + " / " + info.rate + " Hz");
		}
		Ui.end();

		if (voiceOn)
			Audio.voice("lab", snd, {
				loop: true,
				volume: volume,
				pitch: voicePitch,
				pan: pan
			});
		sweepRetired();

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.08, 0.06, 0.12, 1.0])
		});
		Ui.render();
		Gfx.endPass();
	}
}
