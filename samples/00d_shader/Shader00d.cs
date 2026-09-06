// lub の samples/00d_shader の entry。
// 実行: lub samples/00d_shader/Shader00d.csproj (transpile + watch + hot reload)
using System;
using static Lub;

public static class Shader00d
{
    static string vs = "struct VSIn  { float3 pos : POSITION; };\n"
        + "struct VSOut { float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")]\n"
        + "VSOut vs_main(VSIn i) { VSOut o; o.pos = float4(i.pos, 1.0); return o; }\n";

    static string fs = "[shader(\"fragment\")]\n"
        + "float4 fs_main() : SV_Target { return float4(1.0, 0.5, 0.0, 1.0); }\n";

    static bool printed = false;

    public static void OnInit()
    {
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    public static void OnFrame(float dt)
    {
        var s = Gfx.UseShader("test", vs, fs, 1);
        if (!printed && s != null)
        {
            Console.WriteLine("shader compiled: test");
            printed = true;
        }
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.1f, 0.1f, 0.2f, 1.0f },
        });
        Gfx.EndPass();
    }
}
