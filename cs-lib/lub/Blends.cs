// よく使う Blend の組。
// Blend は値 (struct) なので、組を変数に写して field を書き換えても、組そのものは変わらない。
namespace Lub;

public static class Blends
{
    // 半透明: 色は描く値の alpha で混ぜ、alpha は重ねた結果の不透明度になる。
    public static readonly Blend Alpha = new Blend
    {
        Src = Gfx.BlendFactor.SrcAlpha,
        Dst = Gfx.BlendFactor.OneMinusSrcAlpha,
        Op = Gfx.BlendOp.Add,
        SrcAlpha = Gfx.BlendFactor.One,
        DstAlpha = Gfx.BlendFactor.OneMinusSrcAlpha,
        OpAlpha = Gfx.BlendOp.Add,
    };

    // 加算: 色も alpha も足す。
    public static readonly Blend Additive = new Blend
    {
        Src = Gfx.BlendFactor.One,
        Dst = Gfx.BlendFactor.One,
        Op = Gfx.BlendOp.Add,
        SrcAlpha = Gfx.BlendFactor.One,
        DstAlpha = Gfx.BlendFactor.One,
        OpAlpha = Gfx.BlendOp.Add,
    };

    // 乗算: 色も alpha も target の値を掛ける。
    public static readonly Blend Multiply = new Blend
    {
        Src = Gfx.BlendFactor.Dst,
        Dst = Gfx.BlendFactor.Zero,
        Op = Gfx.BlendOp.Add,
        SrcAlpha = Gfx.BlendFactor.DstAlpha,
        DstAlpha = Gfx.BlendFactor.Zero,
        OpAlpha = Gfx.BlendOp.Add,
    };
}
