import lub.Lub;
import lub.Gfx;
import lub.Io;

// glTF mesh (Box.glb) を法線可視化 shader + Y 軸回転 MVP で描く。
class Gltf08 {
	static var tAccum:Float = 0;

	public static function main() {}

	public static function onInit() {
		var backend:String = lua.Os.getenv("LUB_BACKEND");
		if (backend == null)
			backend = "sokol";
		Lub.config({backend: backend});
	}

	// row-major で平 float table に flatten する (Slang の
	// SLANG_MATRIX_LAYOUT_ROW_MAJOR と整合)。
	static function mul4(a:Array<Float>, b:Array<Float>):Array<Float> {
		var r:Array<Float> = [for (_ in 0...16) 0.0];
		for (row in 0...4) {
			for (col in 0...4) {
				var s = 0.0;
				for (k in 0...4) {
					s = s + a[row * 4 + k] * b[k * 4 + col];
				}
				r[row * 4 + col] = s;
			}
		}
		return r;
	}

	static function makeMvp(t:Float):lua.Table<Int, Float> {
		var cs = Math.cos(t);
		var sn = Math.sin(t);
		// model: Y 軸回転
		var m:Array<Float> = [
			 cs, 0.0,  sn, 0.0,
			0.0, 1.0, 0.0, 0.0,
			-sn, 0.0,  cs, 0.0,
			0.0, 0.0, 0.0, 1.0
		];
		// view: translate z = +3 (D3D-style LH: camera at origin looks down +Z;
		// move world +Z so the box sits in front of the camera)
		var v:Array<Float> = [
			1.0, 0.0, 0.0, 0.0,
			0.0, 1.0, 0.0, 0.0,
			0.0, 0.0, 1.0, 3.0,
			0.0, 0.0, 0.0, 1.0
		];
		// proj: perspective f=2.0, aspect=16/9, near=0.1, far=100
		var f = 2.0;
		var aspect = 16.0 / 9.0;
		var nz = 0.1;
		var fz = 100.0;
		var p:Array<Float> = [
			f / aspect, 0.0,            0.0,                  0.0,
			       0.0,   f,            0.0,                  0.0,
			       0.0, 0.0, fz / (fz - nz), -fz * nz / (fz - nz),
			       0.0, 0.0,            1.0,                  0.0
		];
		var vm = mul4(v, m);
		var pvm = mul4(p, vm);
		return lua.Table.fromArray(pvm);
	}

	public static function onFrame() {
		tAccum = tAccum + 0.016;

		var vsR = Io.loadText("samples/08_gltf/data/08_gltf.vs.slang");
		var fsR = Io.loadText("samples/08_gltf/data/08_gltf.fs.slang");
		var vs:String = vsR.text;
		var verVs:Int = vsR.version;
		var fs:String = fsR.text;
		var verFs:Int = fsR.version;
		if (vs == null || fs == null)
			return;
		var s = Gfx.useShader("gltf_sh", vs, fs, verVs ^ verFs);

		var meshR = Io.loadGltf("samples/08_gltf/data/08_box.glb");
		var mesh:Dynamic = meshR.mesh;
		var meshVer:Int = meshR.version;
		if (mesh == null)
			return;

		var verts = Io.interleavePn(mesh);
		var vb = Gfx.useBuffer("gltf_vb", Gfx.VERTEX, verts, meshVer);
		var ib = Gfx.useBuffer("gltf_ib", Gfx.INDEX, mesh.indices, meshVer);

		var mvp = makeMvp(tAccum);

		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.1, 0.1, 0.15, 1.0])
		});
		Gfx.draw(mesh.index_count, {verts: vb, indices: ib, uniforms: {mvp: mvp}}, {
			shader: s,
			depth: true,
			depth_write: true,
			cull: Gfx.BACK
		});
		Gfx.endPass();
	}
}
