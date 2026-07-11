// lub core API の参照専用 stub (--ref)。Lua 出力には含めない。
// 実行時は lub runtime が同名の global table (Gfx, Lub, ...) を注入する
// (flat global → table の橋渡しは lub_shim.lua)。
// メンバー名は lub の Lua wire format (snake_case / lowerCamel) に合わせるため、
// C# naming convention は --no-naming-check で抑制してビルドする。
// out 引数は Lua multi-return を宣言順に受ける。

public class TextureRef
{
}

public class ShaderRef
{
}

public class BufferRef
{
}

public class PassOpts
{
    public TextureRef? target;
    public double[]? clear_color;
}

public class DrawBindings
{
    public BufferRef? verts;
}

public class DrawOpts
{
    public ShaderRef? shader;
    public bool depth;
    public int cull;
    public int blend;
}

public static class Gfx
{
    public static TextureRef? main_tex;
    public static int VERTEX;
    public static int NONE;
    public static int ALPHA;

    public static void begin_pass(PassOpts opts)
    {
    }

    public static void end_pass()
    {
    }

    public static ShaderRef? use_shader(string key, string vs, string fs,
        int version)
    {
        return null;
    }

    public static BufferRef? use_buffer(string key, int type,
        System.Collections.Generic.List<double> data, int version)
    {
        return null;
    }

    public static void draw(int count, DrawBindings bindings, DrawOpts opts)
    {
    }
}

public class ConfigOpts
{
    public string? backend;
    public int width;
    public int height;
}

public static class Lub
{
    public static void config(ConfigOpts opts)
    {
    }

    public static void quit()
    {
    }
}

public static class Input
{
    public static bool key_down(string key)
    {
        return false;
    }

    public static bool key_pressed(string key)
    {
        return false;
    }
}

public static class Io
{
    // Lua 側は (text, version, status) の multi-return。text は ready まで nil
    public static void load_text(string path, out string? text,
        out int version, out string? status)
    {
        text = null;
        version = 0;
        status = null;
    }
}

public class EventData
{
    public string? type;
}

// Lua 標準 os library の参照 stub (getenv のみ)
public static class os
{
    public static string? getenv(string name)
    {
        return null;
    }
}
