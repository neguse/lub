import lub.Audio;
import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Math;
import lubx.Assets;
import lubx.Boot;
import lubx.Camera3d;
import lubx.Sfx;

class Flappy17 {
	static var t:Float = 0;
	static var playerY:Float = 0;
	static var velocityY:Float = 0;
	static var pipeX:Float = 5.0;
	static var gapY:Float = 0;
	static var score:Int = 0;
	static var dead:Bool = false;

	public static function main() {}

	public static function onInit() {
		Boot.config({});
	}

	public static function onFrame(dt:Float) {
		t += dt;

		var s = Assets.shader("cube_shader", "samples/17_flappy/data/cube.vs.slang", "samples/17_flappy/data/cube.fs.slang");
		var b = Assets.floats("cube_verts", Gfx.VERTEX, "samples/17_flappy/data/cube.verts.lua");
		if (s == null || b == null)
			return;

		// keyPressed / mousePressed はフレームラッチされたエッジ検出。
		// タップ (web) は SDL の合成でマウス左ボタンとして届く。

		var flap = Input.keyPressed(Key.Space) || Input.mousePressed();
		if (!dead) {
			if (flap) {
				velocityY = 3.0;
				Audio.play(Sfx.blip(300, 700, 0.09, 0.4));
			}
			velocityY -= 8.0 * dt;
			playerY += velocityY * dt;

			pipeX -= 2.0 * dt;
			if (pipeX < -3.0) {
				pipeX = 5.0;
				gapY = (Math.sin(t * 1.7) * 1.5:Float);
				score++;
				Audio.play(Sfx.blip(660, 990, 0.12, 0.35));
			}

			if (playerY < -3.0 || playerY > 3.0)
				dead = true;
			if (pipeX > -1.0 && pipeX < 1.0) {
				if (playerY > gapY + 1.0 || playerY < gapY - 1.0)
					dead = true;
			}
			if (dead)
				Audio.play(Sfx.noise(0.3, 0.5));

			// 落下速度に pitch が追従する風切り音 (毎フレーム宣言する声)。
			// 宣言をやめれば fade out するので stop 管理は要らない。
			var wind = Math.min(1.0, Math.abs(velocityY) * 0.25);
			Audio.voice("wind", Sfx.noise(0.3, 0.5), {loop: true, volume: 0.05 * wind, pitch: 0.5 + wind});
		} else {
			if (flap) {
				dead = false;
				playerY = 0;
				velocityY = 0;
				pipeX = 5.0;
				score = 0;
			}
		}

		var vp = Camera3d.vp({eye: new Vec3(0, 0, -8), target: new Vec3(0, 0, 0)});

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
