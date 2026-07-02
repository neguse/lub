package lubx;

import lub.Gfx;
import lua.Table;

/**
	RCAS 風のシャープ化ポストプロセス。`run(target, scene, sharpness)` で
	`scene` を鮮鋭化して `target` に描く。sharpness は 0..1。
**/
class Rcas {
	static var VS:String = "struct VSIn { float2 pos : POSITION; float2 uv : TEXCOORD0; };\n"
		+ "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };\n"
		+ "[shader(\"vertex\")]\n"
		+ "VSOut vs_main(VSIn i) {\n"
		+ "    VSOut o;\n"
		+ "    o.pos = float4(i.pos, 0.0, 1.0);\n"
		+ "    o.uv = i.uv;\n"
		+ "    return o;\n"
		+ "}\n";

	static var FS:String = "LUB_TEXTURE2D(scene);\n"
		+ "struct Params { float4 params; };\n"
		+ "ConstantBuffer<Params> u;\n"
		+ "struct FSIn { float2 uv : TEXCOORD0; };\n"
		+ "[shader(\"fragment\")]\n"
		+ "float4 fs_main(FSIn i) : SV_Target {\n"
		+ "    float2 dx = float2(ddx(i.uv).x, 0.0);\n"
		+ "    float2 dy = float2(0.0, ddy(i.uv).y);\n"
		+ "    float3 c = LUB_SAMPLE_LOD(scene, i.uv).rgb;\n"
		+ "    float3 n = LUB_SAMPLE_LOD(scene, i.uv - dy).rgb;\n"
		+ "    float3 s = LUB_SAMPLE_LOD(scene, i.uv + dy).rgb;\n"
		+ "    float3 e = LUB_SAMPLE_LOD(scene, i.uv + dx).rgb;\n"
		+ "    float3 w = LUB_SAMPLE_LOD(scene, i.uv - dx).rgb;\n"
		+ "    float sharpness = saturate(u.params.x);\n"
		+ "    float3 detail = c * 4.0 - (n + s + e + w);\n"
		+ "    return float4(saturate(c + detail * sharpness), 1.0);\n"
		+ "}\n";

	final shaderKey:String;
	final bufferKey:String;
	var shader:Dynamic = null;
	var quad:Dynamic = null;

	public function new(shaderKey:String = "lubx_rcas", bufferKey:String = "lubx_rcas_quad") {
		this.shaderKey = shaderKey;
		this.bufferKey = bufferKey;
	}

	public function ensure():Bool {
		if (quad == null) {
			quad = Gfx.useBuffer(bufferKey, Gfx.VERTEX, Table.fromArray([
				-1.0, -1.0, 0.0, 1.0,
				 1.0, -1.0, 1.0, 1.0,
				 1.0,  1.0, 1.0, 0.0,
				-1.0, -1.0, 0.0, 1.0,
				 1.0,  1.0, 1.0, 0.0,
				-1.0,  1.0, 0.0, 0.0
			]), 1);
		}
		shader = Gfx.useShader(shaderKey, VS, FS, 1);
		return shader != null && quad != null;
	}

	public function run(target:Dynamic, scene:Dynamic, sharpness:Float = 0.2) {
		if (!ensure())
			return;
		Gfx.beginPass({target: target, clear_color: Table.fromArray([0.0, 0.0, 0.0, 1.0])});
		Gfx.draw(6, {
			verts: quad,
			scene: scene,
			uniforms: {params: Table.fromArray([sharpness, 0.0, 0.0, 0.0])}
		}, {shader: shader, depth: false, cull: Gfx.NONE});
		Gfx.endPass();
	}
}
