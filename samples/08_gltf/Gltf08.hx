import lub.Lub;
import lub.Gfx;
import lub.Io;
import lub.Math;

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

	static function makeMvp(t:Float):lua.Table<Int, Float> {
		// model: Y 軸回転
		var model = Mat4.rotateY(-t);
		// view: translate z = +3 (D3D-style LH: camera at origin looks down +Z;
		// move world +Z so the box sits in front of the camera)
		var view = Mat4.translate(new Vec3(0.0, 0.0, 3.0));
		// proj: perspective with focal length f=2.0 directly (not an fov),
		// aspect=16/9, near=0.1, far=100
		var f = 2.0;
		var aspect = 16.0 / 9.0;
		var nz = 0.1;
		var fz = 100.0;
		var proj = Mat4.zero();
		proj.m[0] = f / aspect;
		proj.m[5] = f;
		proj.m[10] = fz / (fz - nz);
		proj.m[11] = -fz * nz / (fz - nz);
		proj.m[14] = 1.0;
		var pvm = proj.mul(view.mul(model));
		return lua.Table.fromArray(pvm.m);
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
