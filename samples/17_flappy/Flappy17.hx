import lub.Audio;
import lub.Lub;
import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Io;
import lub.Math;

class Flappy17 {
	static var t:Float = 0;
	static var playerY:Float = 0;
	static var velocityY:Float = 0;
	static var pipeX:Float = 5.0;
	static var gapY:Float = 0;
	static var score:Int = 0;
	static var dead:Bool = false;

	// SE はコードで合成する (raw PCM → Audio.pcm)。内容 dedupe されるので
	// hot reload で作り直しても同じ snd handle に戻る。
	static var sndFlap:Int = 0;
	static var sndScore:Int = 0;
	static var sndDeath:Int = 0;

	static inline var RATE = 44100;

	// 矩形波 blip。freq0→freq1 へスイープしつつ指数減衰。
	static function blip(freq0:Float, freq1:Float, dur:Float, vol:Float):lua.Table<Int, Float> {
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
		return out;
	}

	// ノイズバースト。乱数は固定シードの xorshift (Math.random だと reload の
	// たびに波形が変わって dedupe が効かない)。
	static function noiseBurst(dur:Float, vol:Float):lua.Table<Int, Float> {
		var n = Std.int(dur * RATE);
		var out = lua.Table.create();
		var seed = 0x12345678;
		var hold = 0.0;
		for (i in 0...n) {
			if (i % 16 == 0) {
				seed ^= seed << 13;
				seed ^= seed >>> 17;
				seed ^= seed << 5;
				hold = (seed & 0xffff) / 32768.0 - 1.0;
			}
			var u = i / n;
			out[i + 1] = hold * Math.exp(-4.0 * u) * vol;
		}
		return out;
	}

	static function synth() {
		if (sndFlap != 0)
			return;
		sndFlap = Audio.pcm(blip(300, 700, 0.09, 0.4), 1, RATE);
		sndScore = Audio.pcm(blip(660, 990, 0.12, 0.35), 1, RATE);
		sndDeath = Audio.pcm(noiseBurst(0.3, 0.5), 1, RATE);
	}

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	public static function onFrame(dt:Float) {
		t += dt;

		var vsResult = Io.loadText("samples/17_flappy/data/cube.vs.slang");
		var fsResult = Io.loadText("samples/17_flappy/data/cube.fs.slang");
		var vertsResult = Io.loadFloats("samples/17_flappy/data/cube.verts.lua");
		if (vsResult.text == null || fsResult.text == null || vertsResult.data == null)
			return;

		var s = Gfx.useShader("cube_shader", vsResult.text, fsResult.text, vsResult.version * 31 + fsResult.version);
		var b = Gfx.useBuffer("cube_verts", Gfx.VERTEX, vertsResult.data, vertsResult.version);

		// keyPressed / mousePressed はフレームラッチされたエッジ検出。
		// タップ (web) は SDL の合成でマウス左ボタンとして届く。
		synth();

		var flap = Input.keyPressed(Key.Space) || Input.mousePressed();
		if (!dead) {
			if (flap) {
				velocityY = 3.0;
				Audio.play(sndFlap);
			}
			velocityY -= 8.0 * dt;
			playerY += velocityY * dt;

			pipeX -= 2.0 * dt;
			if (pipeX < -3.0) {
				pipeX = 5.0;
				gapY = (Math.sin(t * 1.7) * 1.5:Float);
				score++;
				Audio.play(sndScore);
			}

			if (playerY < -3.0 || playerY > 3.0)
				dead = true;
			if (pipeX > -1.0 && pipeX < 1.0) {
				if (playerY > gapY + 1.0 || playerY < gapY - 1.0)
					dead = true;
			}
			if (dead)
				Audio.play(sndDeath);

			// 落下速度に pitch が追従する風切り音 (毎フレーム宣言する声)。
			// 宣言をやめれば fade out するので stop 管理は要らない。
			var wind = Math.min(1.0, Math.abs(velocityY) * 0.25);
			Audio.voice("wind", sndDeath, {loop: true, volume: 0.05 * wind, pitch: 0.5 + wind});
		} else {
			if (flap) {
				dead = false;
				playerY = 0;
				velocityY = 0;
				pipeX = 5.0;
				score = 0;
			}
		}

		var aspect:Float = 1280.0 / 720.0;
		var proj = Mat4.perspectiveLh(60.0, aspect, 0.1, 100.0);
		var view = Mat4.lookAtLh(new Vec3(0, 0, -8), new Vec3(0, 0, 0), new Vec3(0, 1, 0));
		var vp = proj * view;

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.05, 0.05, 0.15, 1.0])
		});

		var drawOpts = {shader: s, depth: true, cull: Gfx.NONE};

		var playerModel = Mat4.translate(new Vec3(-2.0, playerY, 0)) * Mat4.rotateY(t * 3.0) * Mat4.scale(new Vec3(0.4, 0.4, 0.4));
		var playerMvp = vp * playerModel;
		Gfx.draw(36, {verts: b, uniforms: {mvp: lua.Table.fromArray(playerMvp.m)}}, drawOpts);

		var pipeScale = Mat4.scale(new Vec3(0.8, 5.0, 0.8));
		var topModel = Mat4.translate(new Vec3(pipeX, gapY + 3.5, 0)) * pipeScale;
		var topMvp = vp * topModel;
		Gfx.draw(36, {verts: b, uniforms: {mvp: lua.Table.fromArray(topMvp.m)}}, drawOpts);

		var botModel = Mat4.translate(new Vec3(pipeX, gapY - 3.5, 0)) * pipeScale;
		var botMvp = vp * botModel;
		Gfx.draw(36, {verts: b, uniforms: {mvp: lua.Table.fromArray(botMvp.m)}}, drawOpts);

		Gfx.endPass();
	}
}
