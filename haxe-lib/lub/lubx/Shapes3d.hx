package lubx;

import lub.Mesh.MeshData;

/**
	単位プリミティブを `MeshData` 形式(indexed、positions + normals + 白色)で
	生成する。`Mesh3d.rebuild()` にそのまま渡せて、SDF / glTF メッシュと同じ
	描画経路に乗る。着色は draw 側の tint で(白 × tint = tint がそのまま albedo)。

	既存 `Shapes` は sfb / 11_shadow 用の非 indexed・stride 10 生成で、別物。
**/
class Shapes3d {
	static function mesh(positions:Array<Float>, normals:Array<Float>, indices:Array<Int>):MeshData {
		// 頂点色は白 (interleave 既定は 0.8 グレー)。draw 側の tint がそのまま
		// albedo になるように。
		var n = Std.int(positions.length / 3);
		var colors = new Array<Float>();
		colors.resize(n * 3);
		for (i in 0...n * 3)
			colors[i] = 1.0;
		return {
			positions: lua.Table.fromArray(positions),
			normals: lua.Table.fromArray(normals),
			colors: lua.Table.fromArray(colors),
			indices: cast lua.Table.fromArray(indices),
			vert_count: n,
			index_count: indices.length,
		};
	}

	/** `Shapes`(stride 10: pos3 + normal3 + rgba)の生成結果を MeshData に
		変換する。既存の `Shapes.box/quad/sphere` で組んだジオメトリを
		`Mesh3d` / `Renderer3d` に載せるためのブリッジ。alpha は落ちる。 **/
	public static function fromInterleaved(v:Array<Float>):MeshData {
		var n = Std.int(v.length / 10);
		var pos = new Array<Float>();
		var nrm = new Array<Float>();
		var col = new Array<Float>();
		var indices = new Array<Int>();
		pos.resize(n * 3);
		nrm.resize(n * 3);
		col.resize(n * 3);
		indices.resize(n);
		for (i in 0...n) {
			var o = i * 10;
			pos[i * 3] = v[o];
			pos[i * 3 + 1] = v[o + 1];
			pos[i * 3 + 2] = v[o + 2];
			nrm[i * 3] = v[o + 3];
			nrm[i * 3 + 1] = v[o + 4];
			nrm[i * 3 + 2] = v[o + 5];
			col[i * 3] = v[o + 6];
			col[i * 3 + 1] = v[o + 7];
			col[i * 3 + 2] = v[o + 8];
			indices[i] = i;
		}
		return {
			positions: lua.Table.fromArray(pos),
			normals: lua.Table.fromArray(nrm),
			colors: lua.Table.fromArray(col),
			indices: cast lua.Table.fromArray(indices),
			vert_count: n,
			index_count: n,
		};
	}

	/** 辺長 2 の立方体(中心原点、±1)。scale は model 行列で。 **/
	public static function cube():MeshData {
		var pos:Array<Float> = [];
		var nrm:Array<Float> = [];
		var indices:Array<Int> = [];
		var faces = [
			{n: [1.0, 0.0, 0.0], u: [0.0, 1.0, 0.0], v: [0.0, 0.0, 1.0]},
			{n: [-1.0, 0.0, 0.0], u: [0.0, 0.0, 1.0], v: [0.0, 1.0, 0.0]},
			{n: [0.0, 1.0, 0.0], u: [0.0, 0.0, 1.0], v: [1.0, 0.0, 0.0]},
			{n: [0.0, -1.0, 0.0], u: [1.0, 0.0, 0.0], v: [0.0, 0.0, 1.0]},
			{n: [0.0, 0.0, 1.0], u: [1.0, 0.0, 0.0], v: [0.0, 1.0, 0.0]},
			{n: [0.0, 0.0, -1.0], u: [0.0, 1.0, 0.0], v: [1.0, 0.0, 0.0]},
		];
		for (f in faces) {
			var base = Std.int(pos.length / 3);
			for (i in 0...4) {
				var su = (i == 1 || i == 2) ? 1.0 : -1.0;
				var sv = (i >= 2) ? 1.0 : -1.0;
				for (k in 0...3)
					pos.push(f.n[k] + f.u[k] * su + f.v[k] * sv);
				for (k in 0...3)
					nrm.push(f.n[k]);
			}
			for (idx in [0, 1, 2, 0, 2, 3])
				indices.push(base + idx);
		}
		return mesh(pos, nrm, indices);
	}

	/** 高さ 1(y = ±0.5)、半径 1 の円柱。 **/
	public static function cylinder(sides:Int):MeshData {
		var pos:Array<Float> = [];
		var nrm:Array<Float> = [];
		var indices:Array<Int> = [];
		for (i in 0...sides) {
			var a = i / sides * Math.PI * 2.0;
			var nx = Math.cos(a);
			var nz = Math.sin(a);
			pos.push(nx);
			pos.push(-0.5);
			pos.push(nz);
			nrm.push(nx);
			nrm.push(0.0);
			nrm.push(nz);
			pos.push(nx);
			pos.push(0.5);
			pos.push(nz);
			nrm.push(nx);
			nrm.push(0.0);
			nrm.push(nz);
		}
		for (i in 0...sides) {
			var b0 = i * 2;
			var b1 = ((i + 1) % sides) * 2;
			for (idx in [b0, b0 + 1, b1 + 1, b0, b1 + 1, b1])
				indices.push(idx);
		}
		for (side in 0...2) {
			var ny = side == 0 ? 1.0 : -1.0;
			var y = ny * 0.5;
			var center = Std.int(pos.length / 3);
			pos.push(0.0);
			pos.push(y);
			pos.push(0.0);
			nrm.push(0.0);
			nrm.push(ny);
			nrm.push(0.0);
			for (i in 0...sides) {
				var a = i / sides * Math.PI * 2.0;
				pos.push(Math.cos(a));
				pos.push(y);
				pos.push(Math.sin(a));
				nrm.push(0.0);
				nrm.push(ny);
				nrm.push(0.0);
			}
			for (i in 0...sides) {
				var r0 = center + 1 + i;
				var r1 = center + 1 + ((i + 1) % sides);
				if (ny > 0) {
					indices.push(center);
					indices.push(r0);
					indices.push(r1);
				} else {
					indices.push(center);
					indices.push(r1);
					indices.push(r0);
				}
			}
		}
		return mesh(pos, nrm, indices);
	}

	/** 半径 1 の UV 球。 **/
	public static function sphere(stacks:Int, slices:Int):MeshData {
		var pos:Array<Float> = [];
		var nrm:Array<Float> = [];
		var indices:Array<Int> = [];
		for (st in 0...stacks + 1) {
			var phi = st / stacks * Math.PI;
			var y = Math.cos(phi);
			var r = Math.sin(phi);
			for (sl in 0...slices + 1) {
				var th = sl / slices * Math.PI * 2.0;
				var x = r * Math.cos(th);
				var z = r * Math.sin(th);
				pos.push(x);
				pos.push(y);
				pos.push(z);
				nrm.push(x);
				nrm.push(y);
				nrm.push(z);
			}
		}
		for (st in 0...stacks) {
			for (sl in 0...slices) {
				var a = st * (slices + 1) + sl;
				var b = a + slices + 1;
				for (idx in [a, b, a + 1, a + 1, b, b + 1])
					indices.push(idx);
			}
		}
		return mesh(pos, nrm, indices);
	}
}
