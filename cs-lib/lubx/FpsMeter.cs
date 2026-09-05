// 実装ライブラリ lubx の FpsMeter。
// fps の read-only 性は field + doc comment で表す。
// initialFps はデフォルト引数でなく nullable + ?? で受ける (tcs はデフォルト
// 引数値を呼び出し側に埋めないが、Lua の省略引数 = nil が null に落ちる)。

/// <summary>
/// Sys.actual_fps の値を保持するだけの小物。計測が入る前 (起動直後) は
/// initialFps を返し続ける。毎フレーム tick() を呼ぶ。
/// </summary>
using static Lub;

public class FpsMeter
{
    /// <summary>直近の実測 FPS。tick() が更新する (呼び出し側は読み取り専用)。</summary>
    public double Fps;

    public FpsMeter(double? initialFps = null)
    {
        Fps = initialFps ?? 60.0;
    }

    public double Tick()
    {
        var measured = Sys.ActualFps();
        if (measured > 0)
        {
            Fps = measured;
        }
        return Fps;
    }
}
