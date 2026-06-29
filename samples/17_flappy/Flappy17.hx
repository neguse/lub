import lub.Lub;
import lub.Gfx;
import lub.Input;
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

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	public static function onFrame() {
		t += 1.0 / 60.0;

		var vsResult = Io.loadText("samples/17_flappy/data/cube.vs.slang");
		var fsResult = Io.loadText("samples/17_flappy/data/cube.fs.slang");
		var vertsResult = Io.loadFloats("samples/17_flappy/data/cube.verts.lua");
		if (vsResult.text == null || fsResult.text == null || vertsResult.data == null)
			return;

		var s = Gfx.useShader("cube_shader", vsResult.text, fsResult.text, vsResult.version ^ fsResult.version);
		var b = Gfx.useBuffer("cube_verts", Gfx.VERTEX, vertsResult.data, vertsResult.version);

		if (!dead) {
			if (Input.keyDown("space") || Input.mouseDown(0)) {
				velocityY = 3.0;
			}
			velocityY -= 8.0 / 60.0;
			playerY += velocityY / 60.0;

			pipeX -= 2.0 / 60.0;
			if (pipeX < -3.0) {
				pipeX = 5.0;
				gapY = (Math.sin(t * 1.7) * 1.5:Float);
				score++;
			}

			if (playerY < -3.0 || playerY > 3.0)
				dead = true;
			if (pipeX > -1.0 && pipeX < 1.0) {
				if (playerY > gapY + 1.0 || playerY < gapY - 1.0)
					dead = true;
			}
		} else {
			if (Input.keyDown("space") || Input.mouseDown(0)) {
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
		var vp = proj.mul(view);

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.05, 0.05, 0.15, 1.0])
		});

		var drawOpts = {shader: s, depth: true, cull: Gfx.NONE};

		var playerModel = Mat4.translate(new Vec3(-2.0, playerY, 0)).mul(Mat4.rotateY(t * 3.0)).mul(Mat4.scale(new Vec3(0.4, 0.4, 0.4)));
		var playerMvp = vp.mul(playerModel);
		Gfx.draw(36, {verts: b, uniforms: {mvp: lua.Table.fromArray(playerMvp.m)}}, drawOpts);

		var pipeScale = Mat4.scale(new Vec3(0.8, 5.0, 0.8));
		var topModel = Mat4.translate(new Vec3(pipeX, gapY + 3.5, 0)).mul(pipeScale);
		var topMvp = vp.mul(topModel);
		Gfx.draw(36, {verts: b, uniforms: {mvp: lua.Table.fromArray(topMvp.m)}}, drawOpts);

		var botModel = Mat4.translate(new Vec3(pipeX, gapY - 3.5, 0)).mul(pipeScale);
		var botMvp = vp.mul(botModel);
		Gfx.draw(36, {verts: b, uniforms: {mvp: lua.Table.fromArray(botMvp.m)}}, drawOpts);

		Gfx.endPass();
	}
}
