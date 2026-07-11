// lub core API の参照専用 stub (--ref)。Lua 出力には含めない。
// 実行時は lub runtime が同名の global table (Gfx, Lub, ...) を注入する。
// メンバー名は lub の Lua wire format (snake_case / lowerCamel) に合わせるため、
// C# naming convention は --no-naming-check で抑制してビルドする。

public class TextureRef
{
}

public class PassOpts
{
    public TextureRef? target;
    public double[]? clear_color;
}

public static class Gfx
{
    public static TextureRef? main_tex;

    public static void begin_pass(PassOpts opts)
    {
    }

    public static void end_pass()
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
