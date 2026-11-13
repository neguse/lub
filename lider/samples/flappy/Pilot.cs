// 手で跳ばない間の代わりの操作。次のパイプの隙間の中ほどより下へ落ちかけたら跳ぶ。
public class Pilot
{
    // 手で最後に跳んでから、代わりに跳びだすまでの秒数
    const float Idle = 3.0f;

    // 隙間の中ほどから、これだけ下で跳ぶ
    const float Below = 0.3f;

    public float SinceManual = Idle;

    // 代わりに操作している間は、始めるのとやり直すのも代わりにする
    public bool InCharge()
    {
        return SinceManual >= Idle;
    }

    public bool Flap(bool manual, float dt, Bird bird, Pipe? next)
    {
        if (manual)
        {
            SinceManual = 0;
            return true;
        }
        SinceManual += dt;
        if (!InCharge())
            return false;
        var target = next == null ? 0 : next.GapY;
        return bird.Vy <= 0 && bird.Y < target - Below;
    }
}
