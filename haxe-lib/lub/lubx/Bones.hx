package lubx;

import lub.Math;
import lub.Mesh.MeshData;

/**
	skinned SDF メッシュ(`Sdf` の `bone()` ノード)の bone 行列定型。
	規約: shader は `float4x4 bones[8]`、行列は `mesh.bones` の並び順、
	不足分は単位行列で埋める(`Mesh3d` の skinned レイアウトとセットで使う)。
	アニメーション(どの骨をどう回すか)はゲーム側の仕事のまま。
**/
class Bones {
	/** 最大 bone 数(shader 側の `float4x4 bones[8]` と対)。 **/
	public static inline var MAX:Int = 8;

	/** pivot `(px, py, pz)` 回りの回転(model 空間)。T(p) · R · T(−p)。 **/
	public static function pivotRot(px:Float, py:Float, pz:Float, rot:Mat4):Mat4 {
		return Mat4.translate(new Vec3(px, py, pz)).mul(rot.mul(Mat4.translate(new Vec3(-px, -py, -pz))));
	}

	/**
		`mesh.bones` の並び順で `resolve(name, x, y, z)` が返す行列を
		mat4 × 8 = 128 float に詰める。resolve が null を返した bone は単位行列。
		`(x, y, z)` はその bone の pivot(`pivotRot` にそのまま渡せる)。
	**/
	public static function pack(mesh:MeshData, resolve:(name:String, x:Float, y:Float, z:Float) -> Mat4):lua.Table<Int, Float> {
		var arr = new Array<Float>();
		var count = 0;
		if (mesh != null && mesh.bones != null) {
			var i = 1;
			while (count < MAX) {
				var b:Dynamic = mesh.bones[i];
				if (b == null)
					break;
				var m = resolve((b.name : String), (b.x : Float), (b.y : Float), (b.z : Float));
				if (m == null)
					m = new Mat4();
				for (v in m.m)
					arr.push(v);
				count++;
				i++;
			}
		}
		while (count < MAX) {
			var id = new Mat4();
			for (v in id.m)
				arr.push(v);
			count++;
		}
		return lua.Table.fromArray(arr);
	}
}
