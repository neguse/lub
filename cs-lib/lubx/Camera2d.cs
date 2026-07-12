// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Camera2d.hx と対)。
// Haxe 版の匿名戻り値 {x, y} は stub の座標 wire format (Vec2d) で返す。
// Gfx.size / Input.mouse_pos の multi-return は out 引数で受ける。

/// <summary>
/// 2D ワールド座標 (任意単位, y 上向き) と論理スクリーン px (y 下向き) の相互変換。
/// ppm は 1 ワールド単位あたりの px。(originX, originY) はワールド原点のスクリーン位置。
/// </summary>
public class Camera2d
{
    public double ppm;
    public double originX;
    public double originY;
    public double logicalW;
    public double logicalH;

    public Camera2d(double logicalW, double logicalH, double ppm,
        double originX, double originY)
    {
        this.logicalW = logicalW;
        this.logicalH = logicalH;
        this.ppm = ppm;
        this.originX = originX;
        this.originY = originY;
    }

    /// <summary>world x → screen x</summary>
    public double sx(double wx)
    {
        return originX + wx * ppm;
    }

    /// <summary>world y → screen y</summary>
    public double sy(double wy)
    {
        return originY - wy * ppm;
    }

    /// <summary>screen x → world x</summary>
    public double wx(double sxv)
    {
        return (sxv - originX) / ppm;
    }

    /// <summary>screen y → world y</summary>
    public double wy(double syv)
    {
        return (originY - syv) / ppm;
    }

    /// <summary>マウス位置をワールド座標で。実ウィンドウ px → 論理 px 換算込み。</summary>
    public Vec2d mouseWorld()
    {
        Gfx.size(out var gw, out var gh);
        Input.mouse_pos(out var mx, out var my);
        var lx = mx * logicalW / gw;
        var ly = my * logicalH / gh;
        return new Vec2d { x = wx(lx), y = wy(ly) };
    }
}
