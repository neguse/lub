// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/FpsMeter.hx と対)。
// Haxe 版の fps(default, null) 相当の read-only 性は field + doc comment で表す。
// initialFps はデフォルト引数でなく nullable + ?? で受ける (tcs はデフォルト
// 引数値を呼び出し側に埋めないが、Lua の省略引数 = nil が null に落ちる)。

/// <summary>
/// Sys.actual_fps の値を保持するだけの小物。計測が入る前 (起動直後) は
/// initialFps を返し続ける。毎フレーム tick() を呼ぶ。
/// </summary>
public class FpsMeter
{
    /// <summary>直近の実測 FPS。tick() が更新する (呼び出し側は読み取り専用)。</summary>
    public double fps;

    public FpsMeter(double? initialFps = null)
    {
        fps = initialFps ?? 60.0;
    }

    public double tick()
    {
        var measured = Sys.actual_fps();
        if (measured > 0)
        {
            fps = measured;
        }
        return fps;
    }
}
