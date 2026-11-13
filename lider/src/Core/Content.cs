// ウィンドウが映す物 (中身)。
using Lub;

namespace LiderCore;

public interface IContent
{
    // 映す絵 (無ければ null)
    TextureRef? Texture();
}

// ゲームの絵 (GameApp が動かすゲーム)
public class GameView : IContent
{
    public TextureRef? Texture()
    {
        return GameApp.Target;
    }
}
