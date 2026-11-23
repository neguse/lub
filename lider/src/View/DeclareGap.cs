// lub discards textures whose declaration lapsed, and creates an empty texture on the next declaration.
// Boards that redraw only when their contents change need to redraw when their declaration lapsed.
namespace LiderCore;

public class DeclareGap
{
    // lider's frame number (advances even while lider is hidden)
    public static int Frame;
    int last = -1;

    // Call when declaring. true if it was not declared in the previous frame
    public bool Lapsed()
    {
        var lapsed = Frame - last > 1;
        last = Frame;
        return lapsed;
    }
}
