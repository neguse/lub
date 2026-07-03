import lub.Lub;
import lub.Gfx;
import lub.Io;
import lub.Mesh;
import lub.Math;

// SDF モデリング: 距離関数 dist() を Haxe で書き、CPU で grid 評価 →
// surface nets (C 側) でメッシュ化して回して眺める。dist() を書き換えて
// 保存すれば hot reload で再メッシュされる。法線はメッシュではなく SDF の
// 勾配から焼くので、粗い grid でも smin の繋ぎ目が滑らかに出る。
class Sdf19 {
	// grid 解像度と評価範囲 ([-HALF, HALF]^3)。remesh は reload 時に 1 回
	// 走るだけなので、N^3 回の Lua 評価コストはここが上限。
	static inline var N = 56;
	static inline var HALF = 1.45;

	// --- モデル: 雪だるま風 -------------------------------------------------
	static function dist(p:Vec3):Float {
		var body = Sdf.sphere(p - new Vec3(0, -0.42, 0), 0.72);
		var head = Sdf.sphere(p - new Vec3(0, 0.48, 0), 0.46);
		var d = Sdf.smin(body, head, 0.22);
		// X 対称: |x| に折り畳んで片側だけ書く
		var q = new Vec3(Math.abs(p.x), p.y, p.z);
		// 腕: 胴から斜め上へのカプセル
		d = Sdf.smin(d, Sdf.capsule(q, new Vec3(0.56, -0.32, 0), new Vec3(1.04, 0.24, 0), 0.13), 0.10);
		// 目: 球で smooth にくり抜き (camera は -Z 側)。hard な subtract の
		// 鋭い crease は grid 解像度で拾えずギザつくので、縁を blend で丸める。
		d = Sdf.ssub(d, Sdf.sphere(q - new Vec3(0.17, 0.56, -0.40), 0.11), 0.06);
		return d;
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
		var cell = 2 * HALF / (N - 1);
		var g:lua.Table<Int, Float> = lua.Table.create();
		var i = 1;
		for (z in 0...N) {
			var pz = -HALF + z * cell;
			for (y in 0...N) {
				var py = -HALF + y * cell;
				for (x in 0...N) {
					g[i] = dist(new Vec3(-HALF + x * cell, py, pz));
					i++;
				}
			}
		}
		mesh = Mesh.surfaceNets(g, N, N, N, cell, -HALF, -HALF, -HALF);
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

// 距離関数ユーティリティ (iq の distance functions 参照)。
// 汎用性が枯れたら lubx へ昇格させる。
class Sdf {
	public static inline function sphere(p:Vec3, r:Float):Float
		return p.length() - r;

	public static function box(p:Vec3, b:Vec3):Float {
		var qx = Math.abs(p.x) - b.x;
		var qy = Math.abs(p.y) - b.y;
		var qz = Math.abs(p.z) - b.z;
		var ox = Math.max(qx, 0);
		var oy = Math.max(qy, 0);
		var oz = Math.max(qz, 0);
		return Math.sqrt(ox * ox + oy * oy + oz * oz) + Math.min(Math.max(qx, Math.max(qy, qz)), 0);
	}

	/** 線分 `a`-`b` を軸とする半径 `r` のカプセル。 **/
	public static function capsule(p:Vec3, a:Vec3, b:Vec3, r:Float):Float {
		var pa = p - a;
		var ba = b - a;
		var h = MathUtil.clamp(pa.dot(ba) / ba.dot(ba), 0, 1);
		return (pa - ba * h).length() - r;
	}

	/** XZ 平面に寝たトーラス。 **/
	public static function torus(p:Vec3, rMajor:Float, rMinor:Float):Float {
		var qx = Math.sqrt(p.x * p.x + p.z * p.z) - rMajor;
		return Math.sqrt(qx * qx + p.y * p.y) - rMinor;
	}

	/** smooth min (union)。`k` が blend 幅。 **/
	public static function smin(a:Float, b:Float, k:Float):Float {
		var h = MathUtil.clamp(0.5 + 0.5 * (b - a) / k, 0, 1);
		return MathUtil.lerp(b, a, h) - k * h * (1 - h);
	}

	/** `d` から `sub` をくり抜く。 **/
	public static inline function subtract(d:Float, sub:Float):Float
		return Math.max(d, -sub);

	/** smooth subtraction。`k` が縁の丸まり幅。 **/
	public static function ssub(d:Float, sub:Float, k:Float):Float {
		var h = MathUtil.clamp(0.5 - 0.5 * (d + sub) / k, 0, 1);
		return MathUtil.lerp(d, -sub, h) + k * h * (1 - h);
	}
}
