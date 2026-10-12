// backend は OnInit の後に起動する。OnInit の中で backend を使う API
// (UseBuffer / BeginPass / UseTexture) を呼ぶと落ちるのではなく error になる。
// 同じ呼び出しは OnFrame では通る。OnInit は最初の失敗で終わるので、API ごとに
// entry を分ける (TestGfxInOnInitBuffer など)。
using System;
using System.Collections.Generic;
using Lub;

public static class TestGfxInOnInit
{
    static readonly List<float> data = new List<float> { 0, 0, 0, 1, 0, 0, 0, 1, 0 };

    public static void Init(string api)
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
        Check.Error(api + " in OnInit", "OnInit");
        if (api == "UseBuffer")
            Gfx.UseBuffer("init_test", Gfx.BufferType.Storage, data);
        else if (api == "BeginPass")
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        else
            Gfx.UseTexture("init_tex", 2, 2, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
    }

    public static void Frame()
    {
        Check.Frame();
        var b = Gfx.UseBuffer("init_test", Gfx.BufferType.Storage, data);
        Check.That(b != null, "UseBuffer in OnFrame must succeed");
        var t = Gfx.UseTexture("init_tex", 2, 2, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        Check.That(t != null, "UseTexture in OnFrame must succeed");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        Check.Pass();
    }
}

public static class TestGfxInOnInitBuffer
{
    public static void OnInit() { TestGfxInOnInit.Init("UseBuffer"); }
    public static void OnFrame(float dt) { TestGfxInOnInit.Frame(); }
    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

public static class TestGfxInOnInitPass
{
    public static void OnInit() { TestGfxInOnInit.Init("BeginPass"); }
    public static void OnFrame(float dt) { TestGfxInOnInit.Frame(); }
    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

public static class TestGfxInOnInitTexture
{
    public static void OnInit() { TestGfxInOnInit.Init("UseTexture"); }
    public static void OnFrame(float dt) { TestGfxInOnInit.Frame(); }
    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
