// 置かれていない file の load は、最初の呼び出しで error ("missing") になり本体は null。
// native も web も pending を返さない (web は読める file を起動前に MEMFS へ置く)。
using System;
using Lub;

public static class TestIoMissing
{
    const string missing = "tests/cs/data/no_such_file";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Io.LoadText(missing + ".txt", out var text, out _, out var textStatus, out var textError);
        Check.That(text == null && textStatus == Io.Status.Error && textError == "missing", "load_text: " + textError);
        Io.LoadBytes(missing + ".bin", out var bytes, out _, out var bytesStatus, out var bytesError);
        Check.That(bytes == null && bytesStatus == Io.Status.Error && bytesError == "missing", "load_bytes: " + bytesError);
        Io.LoadFloats(missing + ".lua", out var floats, out _, out var floatsStatus, out var floatsError);
        Check.That(floats == null && floatsStatus == Io.Status.Error && floatsError == "missing", "load_floats: " + floatsError);
        Io.LoadGltf(missing + ".gltf", out var mesh, out _, out var meshStatus, out var meshError);
        Check.That(mesh == null && meshStatus == Io.Status.Error && meshError == "missing", "load_gltf: " + meshError);
        Png.Load(missing + ".png", out var png, out _, out _, out _, out _, out _, out var pngStatus, out var pngError);
        Check.That(png == null && pngStatus == Io.Status.Error && pngError == "missing", "png.load: " + pngError);
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
