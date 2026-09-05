import lub.Audio;
import lub.Gfx;
import lubx.Boot;
import lub.Ui;

// サウンドラボ: ImGui で波形パラメータを調整しながら音を作る。
// - 波形はコード合成 (raw PCM → Audio.snd)。key "lab" で毎フレーム宣言し、
//   パラメータが変わったら version を進めて作り直す。古い snd は runtime が
//   退役させ、fade 中の voice が終わってから回収する。
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
	static var pitch = 1.0;
	static var pan = 0.0;
	static var playOnChange = true;
	static var voiceOn = false;
	static var master = 1.0;

	static var snd = 0;
	static var lastKey = "";
	static var samples:lua.Table<Int, Float> = null;
	static var version = 0;

	static final waveNames = ["square", "saw", "triangle", "sine", "noise"];

	public static function main() {}

	public static function onInit() {
		Boot.config({});
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

	// パラメータが変わっていたら波形を作り直して version を進める。snd は毎
	// フレーム同じ key で宣言する (version が同じなら runtime は波形を読まない)。
	// 内容 dedupe があるので同じパラメータに戻せば同じ handle が返る。
	static function ensureSnd():Bool {
		var key = wave + ":" + freq0 + ":" + freq1 + ":" + duration + ":" + decay + ":" + duty;
		var changed = key != lastKey || samples == null;
		if (changed) {
			samples = synth();
			version++;
			lastKey = key;
		}
		snd = Audio.snd("lab", samples, 1, RATE, version);
		return changed;
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

			// pitch は oneshot では発火時に固定 (負値なら末尾から逆再生)、
			// loop voice では鳴っている間もリアルタイムに追従する。
			// 0 で停止、負値で逆再生 (ターンテーブルのつもりで)。
			volume = Ui.slider("volume", volume, 0.0, 1.0);
			pitch = Ui.slider("pitch", pitch, -2.0, 2.0);
			pan = Ui.slider("pan", pan, -1.0, 1.0);
			var changed = ensureSnd();
			var hit = Ui.button("play");
			Ui.sameLine();
			playOnChange = Ui.checkbox("play on change", playOnChange);
			if (hit || (changed && playOnChange && !voiceOn))
				Audio.play(snd, {volume: volume, pitch: pitch, pan: pan});
			Ui.separator();

			// 継続音: ON の間だけ毎フレーム宣言する。OFF で fade out。
			voiceOn = Ui.checkbox("loop voice", voiceOn);
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
				pitch: pitch,
				pan: pan
			});

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.08, 0.06, 0.12, 1.0])
		});
		Ui.render();
		Gfx.endPass();
	}
}
