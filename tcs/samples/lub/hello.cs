// lub の samples/00_hello 相当を TinyC# で書いた entry。
// 変換と実行は samples/lub/run-lub.sh を参照 (--prelude lub_shim.lua 必須)。
// callback 名 (onInit/onEvent/onFrame/onQuit) は lub の module 契約に合わせる。

public static class Hello
{
    public static void onInit()
    {
        System.Console.WriteLine("[tcs] onInit");
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onFrame(double dt)
    {
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.1, 0.1, 0.2, 1.0 },
        });
        Gfx.end_pass();
    }

    public static void onQuit()
    {
        System.Console.WriteLine("[tcs] onQuit");
    }
}
