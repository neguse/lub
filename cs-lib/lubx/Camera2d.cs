// 実装ライブラリ lubx の Camera2d。
// 座標の戻り値は stub の座標 wire format (Vec2d)。
// Gfx.size / Input.mouse_pos の multi-return は out 引数で受ける。

/// <summary>
/// 2D ワールド座標 (任意単位, y 上向き) と論理スクリーン px (y 下向き) の相互変換。
/// ppm は 1 ワールド単位あたりの px。(originX, originY) はワールド原点のスクリーン位置。
/// </summary>
using static Lub;

public class Camera2d
{
    public double Ppm;
    public double OriginX;
    public double OriginY;
    public double LogicalW;
    public double LogicalH;

    public Camera2d(double logicalW, double logicalH, double ppm,
        double originX, double originY)
    {
        this.LogicalW = logicalW;
        this.LogicalH = logicalH;
        this.Ppm = ppm;
        this.OriginX = originX;
        this.OriginY = originY;
    }

    /// <summary>world x → screen x</summary>
    public double Sx(double wx)
    {
        return OriginX + wx * Ppm;
    }

    /// <summary>world y → screen y</summary>
    public double Sy(double wy)
    {
        return OriginY - wy * Ppm;
    }

    /// <summary>screen x → world x</summary>
    public double Wx(double sxv)
    {
        return (sxv - OriginX) / Ppm;
    }

    /// <summary>screen y → world y</summary>
    public double Wy(double syv)
    {
        return (OriginY - syv) / Ppm;
    }

    /// <summary>マウス位置をワールド座標で。実ウィンドウ px → 論理 px 換算込み。</summary>
    public Vec2d MouseWorld()
    {
        Gfx.Size(out var gw, out var gh);
        Input.MousePos(out var mx, out var my);
        var lx = mx * LogicalW / gw;
        var ly = my * LogicalH / gh;
        return new Vec2d { X = Wx(lx), Y = Wy(ly) };
    }
}
