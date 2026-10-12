// Use* の version 規約:
//   * 省略 = 「内容が変わった」宣言。runtime が新しい実効 version を発行する
//   * ref.Version の再主張 = stored と一致して upload を skip する
//   * 実効 version は entry の hot reload を跨いで巻き戻らない
//     (hot reload を跨ぐ検査は Lua の module 差し替えが要るので C# では移していない)
using System;
using System.Collections.Generic;
using Lub;

public static class TestResourceRevision
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    public static void OnFrame(float dt)
    {
        var data = new List<float> { 0.0f, 0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0.0f };

        // 変更宣言のたびに実効 version が変わる
        var b1 = Gfx.UseBuffer("rev_test", Gfx.BufferType.Storage, data);
        Check.That(b1 != null, "ref must carry the effective version");
        var b2 = Gfx.UseBuffer("rev_test", Gfx.BufferType.Storage, data);
        Check.That(b2!.Version != b1!.Version, "declaration must issue a fresh version");

        // ref.Version の再主張は stored と一致する (upload skip 経路)
        var b3 = Gfx.UseBuffer("rev_test", Gfx.BufferType.Storage, data, b2.Version);
        Check.That(b3!.Version == b2.Version, "reassertion must keep the stored version");

        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
