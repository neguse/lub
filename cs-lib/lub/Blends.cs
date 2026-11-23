// Commonly used Blend presets.
// Blend is a value (struct), so copying a preset into a variable and updating its fields does not change the preset itself.
namespace Lub;

public static class Blends
{
    // Translucent: color is blended by the alpha of the drawn value, and alpha becomes the opacity of the composited result.
    public static readonly Blend Alpha = new Blend
    {
        Src = Gfx.BlendFactor.SrcAlpha,
        Dst = Gfx.BlendFactor.OneMinusSrcAlpha,
        Op = Gfx.BlendOp.Add,
        SrcAlpha = Gfx.BlendFactor.One,
        DstAlpha = Gfx.BlendFactor.OneMinusSrcAlpha,
        OpAlpha = Gfx.BlendOp.Add,
    };

    // Additive: adds both color and alpha.
    public static readonly Blend Additive = new Blend
    {
        Src = Gfx.BlendFactor.One,
        Dst = Gfx.BlendFactor.One,
        Op = Gfx.BlendOp.Add,
        SrcAlpha = Gfx.BlendFactor.One,
        DstAlpha = Gfx.BlendFactor.One,
        OpAlpha = Gfx.BlendOp.Add,
    };

    // Multiply: multiplies both color and alpha by the target value.
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
