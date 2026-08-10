// lub の samples/00d_shader (Haxe 版 Shader00d.hx) の TinyC# 版 entry。
// 実行: lub samples/00d_shader/Shader00d.csproj (transpile + watch + hot reload)
using System;

public static class Shader00d
{
    static string vs = "struct VSIn  { float3 pos : POSITION; };\n"
        + "struct VSOut { float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")]\n"
        + "VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; }\n";

    static string fs = "[shader(\"fragment\")]\n"
        + "float4 fs_main() : SV_Target { return float4(1.0, 0.5, 0.0, 1.0); }\n";

    static bool printed = false;

    public static void onInit()
    {
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    public static void onFrame(float dt)
    {
        var s = Gfx.use_shader("test", vs, fs, 1);
        if (!printed && s != null)
        {
            Console.WriteLine("shader compiled: test");
            printed = true;
        }
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        Gfx.end_pass();
    }
}
