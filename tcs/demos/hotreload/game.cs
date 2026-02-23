// Hot reload demo: edit this while host.lua is running.
// tcs --watch will transpile, host.lua will pick up changes.
//
// Pattern: Game is stateless logic. State lives in the host (like lub3d's self).
// On reload, only methods change. State survives.

public class Game
{
    // Called every frame. Returns a string to display.
    // 'frame' is passed from the host to avoid static state.
    public static string Update(int frame)
    {
        return $"[frame {frame}] Hello from TinyC#!";
    }
}
