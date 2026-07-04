import lub.Lub;
import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Io;
import lub.Mesh;
import lub.Math;
import lub.Ui;
import lubx.Sdf;

// SDF モデリング: lubx.Sdf の builder でツリーを組み、C 側 (sdf_mesh) が
// 評価 → surface nets でメッシュ化する。model() を書き換えて保存すれば
// hot reload で再メッシュされる。法線と材質 (paint) は SDF から頂点に
// 焼くので、UV は存在しない。
// Space キーでメタル変身 (材質の runtime override + matcap)。ジオメトリと
// 焼いた頂点材質は無傷のまま、uniform だけで見た目が変わる。
// (ツリーで書けない SDF は Mesh.surfaceNets に手埋めの grid を渡す経路もある)
class Sdf19 {
	// 最長軸の grid cell 数。bounds はツリーの AABB から自動で決まる。
	static inline var N = 64;

	// --- チューニング対象パラメータ (Ui パネルと hot reload の両方で編集可) --
	static var bodyR = 0.72;
	static var headR = 0.46;
	static var blendK = 0.22;

	// --- モデル: 雪だるま風 -------------------------------------------------
	static function model():SdfNode {
		var body = Sdf.sphere(bodyR).move(0, -0.42, 0);
		var head = Sdf.sphere(headR).move(0, 0.48, 0);
		// 腕: 胴から斜め上へのカプセル。mirrorX で左右対称に
		var arm = Sdf.capsule(new Vec3(0.56, -0.32, 0), new Vec3(1.04, 0.24, 0), 0.13).mirrorX();
		// 目: 球で smooth にくり抜き (camera は -Z 側)。切断面には cutter の
		// 材質が出るので、目玉の色は「彫る球の paint」で決まる
		var eye = Sdf.sphere(0.11).move(0.17, 0.56, -0.40).mirrorX().paint(0x1E2130, 0.0, 0.15);
		return body.smin(head, blendK).smin(arm, 0.10).paint(0xE58B52).ssub(eye, 0.06);
	}

	// --- メッシュ化 ----------------------------------------------------------
	static var mesh:MeshData = null;
	static var meshVer = 0;
	static var tAccum = 0.0;
	// メタル変身 (0..1)。target を Space でトグルして毎フレーム補間
	static var metalT = 0.0;
	static var metalTarget = 0.0;
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

	static var matcapPx:lua.Table<Int, Int> = null;

	static function remesh() {
		mesh = Sdf.mesh(model(), N);
		matcapPx = makeMatcap(64); // reload と同じタイミングで作り直す
		// version は「同じ key で違う内容」を区別できればよいので CPU 時刻 (ms)
		// を使う (remesh 自体が 1ms 以上かかるので衝突しない)。
		meshVer = Std.int(lua.Os.clock() * 1000);
		meshDirty = false;
	}

	// メタルの映り込み用 matcap (sphere map) を手続き生成する。
	// 空→地面の縦グラデ + キーライトのハイライト + 地面の照り返し。
	// アセットファイル不要で、いじって保存すれば hot reload で反映される。
	static function makeMatcap(size:Int):lua.Table<Int, Int> {
		var px:lua.Table<Int, Int> = lua.Table.create();
		var i = 1;
		var lx = -0.45, ly = 0.65, lz = 0.61; // key light (正規化済み)
		inline function b(v:Float):Int
			return Std.int(MathUtil.clamp(v, 0, 1) * 255);
		for (y in 0...size) {
			for (x in 0...size) {
				var nx = (x + 0.5) / size * 2 - 1;
				var ny = 1 - (y + 0.5) / size * 2; // 画像上方向 = +y
				var d2 = nx * nx + ny * ny;
				if (d2 > 1) { // 円の外周は縁の値で延長 (LINEAR filter の黒縁防止)
					var d = Math.sqrt(d2);
					nx /= d;
					ny /= d;
					d2 = 1;
				}
				var nz = Math.sqrt(1 - d2);
				// chrome 風: 地平線でパキッと分かれる空/地面 + キーライト。
				// 金属の説得力はコントラストで決まる
				var horizon = MathUtil.smoothstep(-0.08, 0.12, ny);
				var zen = Math.max(0, ny);
				var r = MathUtil.lerp(0.10, 0.60, horizon) + zen * 0.26;
				var g = MathUtil.lerp(0.09, 0.70, horizon) + zen * 0.20;
				var bl = MathUtil.lerp(0.11, 0.86, horizon) + zen * 0.13;
				var ndl = Math.max(0, nx * lx + ny * ly + nz * lz);
				var spec = Math.pow(ndl, 48) * 1.2;
				px[i] = b(r + spec);
				px[i + 1] = b(g + spec);
				px[i + 2] = b(bl + spec);
				px[i + 3] = 255;
				i += 4;
			}
		}
		return px;
	}

	public static function onFrame() {
		tAccum = tAccum + 0.016;
		if (Input.keyPressed(Key.Space))
			metalTarget = 1.0 - metalTarget;
		metalT = metalT + (metalTarget - metalT) * 0.12;

		var vsR = Io.loadText("samples/19_sdf/data/19_sdf.vs.slang");
		var fsR = Io.loadText("samples/19_sdf/data/19_sdf.fs.slang");
		if (vsR.text == null || fsR.text == null)
			return;
		var s = Gfx.useShader("sdf_sh", vsR.text, fsR.text, vsR.version * 31 + fsR.version);

		// debug UI: いじったフレームだけ remesh (C 評価 ~10ms なのでドラッグ追従)
		Ui.setNextWindow(10, 10, 250, 140);
		if (Ui.begin("sdf tuning")) {
			var nb = Ui.slider("body", bodyR, 0.3, 1.1);
			var nh = Ui.slider("head", headR, 0.2, 0.8);
			var nk = Ui.slider("blend", blendK, 0.01, 0.6);
			if (nb != bodyR || nh != headR || nk != blendK) {
				bodyR = nb;
				headR = nh;
				blendK = nk;
				meshDirty = true;
			}
			metalTarget = Ui.slider("metal (Space)", metalTarget, 0, 1);
			if (mesh != null)
				Ui.text("verts: " + mesh.vert_count);
		}
		Ui.end();

		if (meshDirty)
			remesh();
		var verts = Io.interleavePncm(mesh);
		var vb = Gfx.useBuffer("sdf_vb", Gfx.VERTEX, verts, meshVer);
		var ib = Gfx.useBuffer("sdf_ib", Gfx.INDEX, mesh.indices, meshVer);
		var matcap = Gfx.useTexture("sdf_matcap", 64, 64, Gfx.RGBA8, matcapPx, meshVer);

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
			matcap: matcap,
			uniforms: {
				mvp: lua.Table.fromArray(mvp.m),
				model: lua.Table.fromArray(model.m),
				view: lua.Table.fromArray(view.m),
				// メタル変身の override。ジオメトリにも頂点にも触らない
				params: lua.Table.fromArray([metalT, 0.0, 0.0, 0.0])
			}
		}, {
			shader: s,
			depth: true,
			depth_write: true,
			cull: Gfx.BACK
		});
		Ui.render();
		Gfx.endPass();
	}
}
