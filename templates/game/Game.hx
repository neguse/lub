import lub.Lub;
import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Io;
import lub.Math;

class Game {
	static var t:Float = 0;
	static var playerY:Float = 0;
	static var velocityY:Float = 0;
	static var pipeX:Float = 5.0;
	static var gapY:Float = 0;
	static var score:Int = 0;
	static var dead:Bool = false;

	public static function main() {}

	public static function onInit() {
		Lub.config({});
	}

	// dt は直近フレームの実測秒。固定レート前提にせず dt でスケールする。
	public static function onFrame(dt:Float) {
		t += dt;

		var vsResult = Io.loadText("templates/game/data/cube.vs.slang");
		var fsResult = Io.loadText("templates/game/data/cube.fs.slang");
		var vertsResult = Io.loadFloats("templates/game/data/cube.verts.lua");
		if (vsResult.text == null || fsResult.text == null || vertsResult.data == null)
			return;

		// version はコンテンツハッシュなので、順序依存の結合で合成する。
		var s = Gfx.useShader("cube_shader", vsResult.text, fsResult.text, vsResult.version * 31 + fsResult.version);
		var b = Gfx.useBuffer("cube_verts", Gfx.VERTEX, vertsResult.data, vertsResult.version);

		// keyPressed / mousePressed はフレームラッチされたエッジ検出。
		// タップ (web) は SDL の合成でマウス左ボタンとして届く。
		var flap = Input.keyPressed(Key.Space) || Input.mousePressed();
		if (!dead) {
			if (flap) {
				velocityY = 3.0;
			}
			velocityY -= 8.0 * dt;
			playerY += velocityY * dt;

			pipeX -= 2.0 * dt;
			if (pipeX < -3.0) {
				pipeX = 5.0;
				gapY = (Math.sin(t * 1.7) * 1.5 : Float);
				score++;
			}

			if (playerY < -3.0 || playerY > 3.0)
				dead = true;
			if (pipeX > -1.0 && pipeX < 1.0) {
				if (playerY > gapY + 1.0 || playerY < gapY - 1.0)
					dead = true;
			}
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
