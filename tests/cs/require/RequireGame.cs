// Sys.Require で、source のディレクトリにある Lua の module と native の module (.so) を読み、*.ref.cs に宣言した型で呼ぶ。
// *.ref.cs は tcs の --ref (型だけ) で渡り、Lua には出ない。
// native gate がこのディレクトリを一時ディレクトリに写し、tests/c/lua_native_module.c を build した lubnative.so を置いて起動する。
using System;
using Lub;

public static class RequireGame
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        Sys.Require("require_helper");
        Expect(RequireHelper.Api.Twice(21) == 42, "the Lua module was not called");
        Sys.Require("lubnative");
        Expect(LubNative.Api.Add(40, 2) == 42, "the native module was not called");
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Expect(bool ok, string message)
    {
        if (ok)
            return;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }

    public static ErrorAction OnError(string message)
    {
        Console.WriteLine("FAIL " + message);
        return ErrorAction.Abort;
    }
}
