// Version rules of Use*:
//   * Omitted = a "contents changed" declaration. The runtime issues a new effective version
//   * Reasserting ref.Version = matches stored and skips the upload
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

        // The effective version changes with each change declaration
        var b1 = Gfx.UseBuffer("rev_test", Gfx.BufferType.Storage, data);
        Check.That(b1 != null, "ref must carry the effective version");
        var b2 = Gfx.UseBuffer("rev_test", Gfx.BufferType.Storage, data);
        Check.That(b2!.Version != b1!.Version, "declaration must issue a fresh version");

        // Reasserting ref.Version matches stored (upload skip path)
        var b3 = Gfx.UseBuffer("rev_test", Gfx.BufferType.Storage, data, b2.Version);
        Check.That(b3!.Version == b2.Version, "reassertion must keep the stored version");

        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
