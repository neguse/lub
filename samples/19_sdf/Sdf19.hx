import lub.Gfx;
import lub.Input;
import lub.Input.Key;
import lub.Io;
import lub.Math;
import lub.Ui;
import lubx.Bones;
import lubx.Boot;
import lubx.Mesh3d;
import lubx.Renderer3d;
import lubx.Sdf;
import lubx.SdfPanel;

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

	// --- モデル: 雪だるま風 -------------------------------------------------
	// bone(name, pivot) を付けた部位には skinning 重みが焼かれる。動かす腕は
	// mirror だと pivot が片側になるので左右を個別に置く(目は動かないので
	// mirror のまま)。
	static function model():SdfNode {
		var body = Sdf.sphere(0.72).move(0, -0.42, 0).bone("body", new Vec3(0, -0.42, 0));
		var head = Sdf.sphere(0.46).move(0, 0.48, 0).bone("head", new Vec3(0, 0.10, 0));
		var armL = Sdf.capsule(new Vec3(0.56, -0.32, 0), new Vec3(1.04, 0.24, 0), 0.13).bone("arm_l", new Vec3(0.56, -0.32, 0));
		var armR = Sdf.capsule(new Vec3(-0.56, -0.32, 0), new Vec3(-1.04, 0.24, 0), 0.13).bone("arm_r", new Vec3(-0.56, -0.32, 0));
		// 目: 球で smooth にくり抜き (camera は -Z 側)。切断面には cutter の
		// 材質が出るので、目玉の色は「彫る球の paint」で決まる
		var eye = Sdf.sphere(0.11).move(0.17, 0.56, -0.40).mirrorX().paint(0x1E2130, 0.0, 0.15);
		return body.smin(head, 0.22).smin(armL, 0.10).smin(armR, 0.10).paint(0xE58B52).ssub(eye, 0.06);
	}

	// --- アニメーション -------------------------------------------------------
	static var waveOn = true;

	// mesh.bones の順で 8 本分の行列を詰める (規約は lubx.Bones)
	static function packBones(t:Float):lua.Table<Int, Float> {
		var wave = waveOn ? Math.sin(t * 4.0) * 0.5 : 0.0;
		var nod = waveOn ? Math.sin(t * 2.0) * 0.10 : 0.0;
		return Bones.pack(mesh.data, (name, x, y, z) -> switch (name) {
			case "arm_l": Bones.pivotRot(x, y, z, Mat4.rotateZ(wave));
			case "arm_r": Bones.pivotRot(x, y, z, Mat4.rotateZ(-wave));
			case "head": Bones.pivotRot(x, y, z, Mat4.rotateZ(nod));
			case _: null;
		});
	}

	// --- メッシュ化 ----------------------------------------------------------
	static var mesh = new Mesh3d("sdf19");
	static var tAccum = 0.0;
	// メタル変身 (0..1)。target を Space でトグルして毎フレーム補間
	static var metalT = 0.0;
	static var metalTarget = 0.0;
	// hot reload (lume.hotswap) は「新モジュールの非 nil static」だけを
	// 上書きする (nil は pairs で列挙されないので mesh = null は戻らない)。
	// 初期値 true のフラグがリロード毎に true へ戻るのをトリガに使う。
	// treeDirty = コードからツリーを再構築 (reload 時)、meshDirty = 再評価
	// (SdfPanel での編集時)。パネル編集はツリー (data) に直接乗るので、
	// リロードするまで生きる。
	static var tree:SdfNode = null;
	static var treeDirty = true;
	static var meshDirty = true;

	public static function main() {}

	public static function onInit() {
		Boot.config({});
	}

	static var ren = new Renderer3d("sdf19");
	static var renConfigured = false;

	static var matcapPx:lua.Table<Int, Int> = null;
	static var matcapTex:Dynamic = null;
	static var matcapDirty = true;

	static function remesh() {
		mesh.rebuild(Sdf.mesh(tree, N));
		matcapPx = makeMatcap(64); // reload と同じタイミングで作り直す
		matcapDirty = true;
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

	public static function onFrame(dt:Float) {
		// Preserve the former 0.016-per-frame animation speed at 60 Hz.
		tAccum = tAccum + dt * 0.96;
		if (Input.keyPressed(Key.Space))
			metalTarget = 1.0 - metalTarget;
		// Convert the former 0.12-per-frame blend to an elapsed-time coefficient.
		var metalBlend = 1.0 - Math.pow(0.88, dt * 60.0);
		metalT = metalT + (metalTarget - metalT) * metalBlend;

		var vsR = Io.loadText("samples/19_sdf/data/19_sdf.vs.slang");
		var fsR = Io.loadText("samples/19_sdf/data/19_sdf.fs.slang");
		if (vsR.text == null || fsR.text == null)
			return;
		var s = Gfx.useShader("sdf_sh", vsR.text, fsR.text, vsR.version * 31 + fsR.version);

		if (treeDirty) {
			tree = model(); // コードが source of truth。reload でパネル編集は破棄
			treeDirty = false;
			meshDirty = true;
		}

		// debug UI: ツリー (data) から自動生成したパネル。いじったフレームだけ
		// remesh (C 評価 ~10ms なのでドラッグ追従)。golden capture 中は描画しない
		// (imgui テキストの AA が WARP で ±1 揺れて byte 比較が非決定になるため。
		// 3D シーン本体は決定的)。LUB_GOLDEN は scripts/run-golden.sh がセット。
		if (lua.Os.getenv("LUB_GOLDEN") == null) {
			Ui.setNextWindow(10, 10, 300, 460);
			if (Ui.begin("sdf tuning")) {
				if (SdfPanel.draw(tree))
					meshDirty = true;
				Ui.separator();
				metalTarget = Ui.slider("metal (Space)", metalTarget, 0, 1);
				waveOn = Ui.checkbox("wave", waveOn);
				if (mesh.ready())
					Ui.text("verts: " + mesh.data.vert_count);
			}
			Ui.end();
		}

		if (meshDirty)
			remesh();
		// dirty なら変更宣言(version 省略)、そうでなければ ref.version で再主張。
		matcapTex = Gfx.useTexture("sdf_matcap", 64, 64, Gfx.RGBA8, matcapPx, (matcapDirty || matcapTex == null) ? null : matcapTex.version);
		matcapDirty = false;
		var matcap = matcapTex;

		if (!renConfigured) {
			ren.background = lubx.Color.rgb(0.09, 0.09, 0.12);
			renConfigured = true;
		}
		var model = Mat4.rotateY(tAccum * 0.7);
		ren.begin({
			eye: new Vec3(0.0, 0.55, -3.1),
			target: new Vec3(0, 0.05, 0),
			fov: 45,
			near: 0.1,
			far: 100,
		});
		// material は matcap shader に差し替え (ファイル編集で hot reload)。
		// 追加の view / params / matcap は opts 経由で渡す。
		ren.draw(mesh, model, {
			shader: s,
			textures: {matcap: matcap},
			uniforms: {
				view: lua.Table.fromArray(ren.viewMat.m),
				// メタル変身の override。ジオメトリにも頂点にも触らない
				params: lua.Table.fromArray([metalT, 0.0, 0.0, 0.0]),
			},
			bones: packBones(tAccum),
		});
		ren.end();

		Gfx.beginPass({target: Gfx.mainTex, load: Gfx.LOAD});
		Ui.render();
		Gfx.endPass();
	}
}
