import lub.Gfx;
import lub.Math;
import lubx.Boot;
import lubx.Bones;
import lubx.Color;
import lubx.Mesh3d;
import lubx.Renderer3d;
import lubx.Sdf;
import lubx.Shapes3d;

/**
	lubx.Renderer3d の最小デモ。プリミティブ (床・箱・円柱・球) と
	skinned SDF キャラを投げるだけで、影 + hemispheric ambient + AgX tonemap
	の絵が出ることを見せる。ポーズは時刻からの決定的アニメ (乱数なし)。
**/
class RendererDemo26 {
	static inline var DT = 1.0 / 60.0;
	static var t = 0.0;

	static var ren = new Renderer3d("demo26");
	static var cube = new Mesh3d("demo26_cube");
	static var cyl = new Mesh3d("demo26_cyl");
	static var sph = new Mesh3d("demo26_sph");
	static var chara = new Mesh3d("demo26_chara");
	static var built = false;

	public static function main() {}

	public static function onInit() {
		Boot.config();
	}

	// bone 付き SDF 雪だるま。腕を振る。
	static function charaModel():SdfNode {
		var body = Sdf.sphere(0.5).move(0, 0.5, 0).bone("body", new Vec3(0, 0.3, 0));
		var head = Sdf.sphere(0.32).move(0, 1.05, 0).bone("head", new Vec3(0, 0.8, 0));
		var armL = Sdf.capsule(new Vec3(0.42, 0.75, 0), new Vec3(0.95, 1.05, 0), 0.09).bone("arm_l", new Vec3(0.42, 0.75, 0));
		var armR = Sdf.capsule(new Vec3(-0.42, 0.75, 0), new Vec3(-0.95, 1.05, 0), 0.09).bone("arm_r", new Vec3(-0.42, 0.75, 0));
		var trunk = body.smin(head, 0.08).smin(armL, 0.05).smin(armR, 0.05).paint(0xF2EEE6);
		var eye = Sdf.sphere(0.045).move(0.11, 1.14, -0.27).mirrorX().paint(0x24211E, 0.0, 0.3);
		var nose = Sdf.capsule(new Vec3(0, 1.02, -0.30), new Vec3(0, 1.0, -0.48), 0.05).paint(0xE07830);
		return trunk.union(eye).union(nose);
	}

	static function build() {
		if (built)
			return;
		cube.rebuild(Shapes3d.cube());
		cyl.rebuild(Shapes3d.cylinder(28));
		sph.rebuild(Shapes3d.sphere(14, 24));
		chara.rebuild(Sdf.mesh(charaModel(), 48));
		built = true;
	}

	public static function onFrame() {
		t += DT;
		build();

		ren.shadow.extent = 7.0;
		if (lua.Os.getenv("LUB_R3D_NOSSAO") != null)
			ren.ssao.enabled = false;
		ren.debugView = lua.Os.getenv("LUB_R3D_DEBUG");
		if (lua.Os.getenv("LUB_R3D_STYLE") != null) {
			ren.fog = {color: Color.rgb(0.55, 0.6, 0.7), density: 0.045};
			ren.outline = {color: Color.rgb(0.1, 0.08, 0.12), threshold: 0.4};
			ren.vignette = 0.35;
		}
		ren.begin({
			eye: new Vec3(Math.cos(t * 0.3) * 7.5, 4.2, Math.sin(t * 0.3) * 7.5),
			target: new Vec3(0, 0.7, 0),
			fov: 42,
		});

		// 床 (薄い箱)
		ren.draw(cube, Mat4.translate(new Vec3(0, -0.1, 0)) * Mat4.scale(new Vec3(5.5, 0.1, 5.5)), {tint: Color.hex(0x76816F)});
		// 箱・円柱・球
		ren.draw(cube, Mat4.translate(new Vec3(-2.2, 0.5, 1.2)) * Mat4.rotateY(t * 0.7) * Mat4.scale(new Vec3(0.5, 0.5, 0.5)), {tint: Color.hex(0xE8A33D)});
		ren.draw(cyl, Mat4.translate(new Vec3(2.1, 0.6, 1.4)) * Mat4.scale(new Vec3(0.45, 1.2, 0.45)), {tint: Color.hex(0x4FB8C4)});
		ren.draw(sph, Mat4.translate(new Vec3(1.6, 0.55 + Math.abs(Math.sin(t * 2.0)) * 0.8, -1.6)) * Mat4.scale(new Vec3(0.55, 0.55, 0.55)),
			{tint: Color.hex(0xE85C5C)});
		// 半透明の板
		ren.draw(cube, Mat4.translate(new Vec3(0, 0.9, 2.6)) * Mat4.scale(new Vec3(1.6, 0.9, 0.04)),
			{tint: Color.rgb(0.55, 0.75, 0.95, 0.35), blend: Gfx.ALPHA});

		// 高輝度ランプ (bloom が拾う)
		ren.draw(sph, Mat4.translate(new Vec3(-1.9, 2.6, -1.9)) * Mat4.scale(new Vec3(0.22, 0.22, 0.22)), {tint: Color.rgb(5.0, 4.2, 2.4)});

		// skinned キャラ (腕振り)
		var wave = Math.sin(t * 3.0) * 0.5;
		var bones = Bones.pack(chara.data, (name, x, y, z) -> switch (name) {
			case "arm_l": Bones.pivotRot(x, y, z, Mat4.rotateZ(0.3 + wave * 0.6));
			case "arm_r": Bones.pivotRot(x, y, z, Mat4.rotateZ(-0.3 + wave * 0.6));
			case "head": Bones.pivotRot(x, y, z, Mat4.rotateX(Math.sin(t * 1.7) * 0.12));
			case _: null;
		});
		ren.draw(chara, Mat4.translate(new Vec3(0, 0, 0)) * Mat4.rotateY(Math.PI), {bones: bones});

		ren.end();
	}
}
