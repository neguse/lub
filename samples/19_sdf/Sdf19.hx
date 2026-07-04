import lub.Lub;
import lub.Gfx;
import lub.Io;
import lub.Mesh;
import lub.Math;
import lubx.Sdf;

// SDF モデリング: lubx.Sdf の builder でツリーを組み、C 側 (sdf_mesh) が
// 評価 → surface nets でメッシュ化する。model() を書き換えて保存すれば
// hot reload で再メッシュされる。法線はメッシュではなく SDF の勾配から
// 焼くので、粗い grid でも smin の繋ぎ目が滑らかに出る。
// (ツリーで書けない SDF は Mesh.surfaceNets に手埋めの grid を渡す経路もある)
class Sdf19 {
	// 最長軸の grid cell 数。bounds はツリーの AABB から自動で決まる。
	static inline var N = 64;

	// --- モデル: 雪だるま風 -------------------------------------------------
	static function model():SdfNode {
		var body = Sdf.sphere(0.72).move(0, -0.42, 0);
		var head = Sdf.sphere(0.46).move(0, 0.48, 0);
		// 腕: 胴から斜め上へのカプセル。mirrorX で左右対称に
		var arm = Sdf.capsule(new Vec3(0.56, -0.32, 0), new Vec3(1.04, 0.24, 0), 0.13).mirrorX();
		// 目: 球で smooth にくり抜き (camera は -Z 側)。hard な subtract の
		// 鋭い crease は grid 解像度で拾えずギザつくので、縁を blend で丸める
		var eye = Sdf.sphere(0.11).move(0.17, 0.56, -0.40).mirrorX();
		return body.smin(head, 0.22).smin(arm, 0.10).ssub(eye, 0.06);
	}

	// --- メッシュ化 ----------------------------------------------------------
	static var mesh:MeshData = null;
	static var meshVer = 0;
	static var tAccum = 0.0;
	// hot reload (lume.hotswap) は「新モジュールの非 nil static」だけを
	// 上書きする (nil は pairs で列挙されないので mesh = null は戻らない)。
	// 初期値 true のこのフラグがリロード毎に true へ戻るのを remesh の
	// トリガに使う。
	static var meshDirty = true;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	static function remesh() {
		mesh = Sdf.mesh(model(), N);
		// version は「同じ key で違う内容」を区別できればよいので CPU 時刻 (ms)
		// を使う (remesh 自体が 1ms 以上かかるので衝突しない)。
		meshVer = Std.int(lua.Os.clock() * 1000);
		meshDirty = false;
	}

	public static function onFrame() {
		tAccum = tAccum + 0.016;

		var vsR = Io.loadText("samples/19_sdf/data/19_sdf.vs.slang");
		var fsR = Io.loadText("samples/19_sdf/data/19_sdf.fs.slang");
		if (vsR.text == null || fsR.text == null)
			return;
		var s = Gfx.useShader("sdf_sh", vsR.text, fsR.text, vsR.version * 31 + fsR.version);

		if (meshDirty)
			remesh();
		var verts = Io.interleavePn(mesh);
		var vb = Gfx.useBuffer("sdf_vb", Gfx.VERTEX, verts, meshVer);
		var ib = Gfx.useBuffer("sdf_ib", Gfx.INDEX, mesh.indices, meshVer);

		var sz = Gfx.size();
		var model = Mat4.rotateY(tAccum * 0.7);
		var view = Mat4.lookAtLh(new Vec3(0.0, 0.55, -3.1), new Vec3(0, 0.05, 0), Vec3.up());
		var proj = Mat4.perspectiveLh(45, sz.w / sz.h, 0.1, 100);
		var mvp = proj.mul(view.mul(model));

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.09, 0.09, 0.12, 1.0])
		});
		Gfx.draw(mesh.index_count, {
			verts: vb,
			indices: ib,
			uniforms: {mvp: lua.Table.fromArray(mvp.m), model: lua.Table.fromArray(model.m)}
		}, {
			shader: s,
			depth: true,
			depth_write: true,
			cull: Gfx.BACK
		});
		Gfx.endPass();
	}
}
