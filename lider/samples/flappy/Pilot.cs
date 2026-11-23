// Stand-in control while nobody jumps by hand. Jumps when about to fall below the middle of the next pipe's gap.
public class Pilot
{
    // Seconds after the last manual jump before it starts jumping instead
    const float Idle = 3.0f;

    // Jumps this far below the middle of the gap
    const float Below = 0.3f;

    public float SinceManual = Idle;

    // While it is in control, it also starts and restarts the game in the player's place
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
