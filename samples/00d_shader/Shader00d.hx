import lub.Gfx;
import lua.Lua;

class Shader00d {
	static var vs:String = "struct VSIn  { float3 pos : POSITION; };\n"
		+ "struct VSOut { float4 pos : SV_Position; };\n"
		+ "[shader(\"vertex\")]\n"
		+ "VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; }\n";

	static var fs:String = "[shader(\"fragment\")]\n" + "float4 fs_main() : SV_Target { return float4(1.0, 0.5, 0.0, 1.0); }\n";

	static var printed:Bool = false;

	public static function main() {}

	public static function onInit() {}

	public static function onEvent(e:Dynamic) {}

	public static function onQuit() {}

	public static function onFrame() {
		var s:Dynamic = Gfx.useShader("test", vs, fs, 1);
		if (!printed && s != null && s.__lub_kind == "shader") {
			Lua.print("shader compiled:", s.key);
			printed = true;
		}
		Gfx.beginPass({
			target: Gfx.mainTex,
			clear_color: lua.Table.fromArray([0.1, 0.1, 0.2, 1.0])
		});
		Gfx.endPass();
	}
}
