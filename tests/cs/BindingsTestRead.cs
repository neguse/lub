// Bindings の test の readback。
// tcs2c の経路は Readback に対応していないので BindingsTestUtil と分ける。
using Lub;

public static class BindingsTestRead
{
    // id の結果が届くまで null。
    public static Bytes? Read(Readback rb, TextureRef rt, int? id, out int stride)
    {
        Gfx.ReadTexture(rb, rt, id, out var st, out var bytes, out _, out _, out _, out stride, out var got, out _, out var err);
        Check.That(st != Gfx.ReadbackStatus.Error, "read_texture failed: " + err);
        if (st != Gfx.ReadbackStatus.Ready || id != null)
            return null;
        return bytes;
    }

    public static bool Is(Bytes bytes, int offset, int r, int g, int b)
    {
        return Near(bytes.Get(offset), r) && Near(bytes.Get(offset + 1), g) && Near(bytes.Get(offset + 2), b);
    }

    static bool Near(int v, int want)
    {
        return v >= want - 2 && v <= want + 2;
    }
}
