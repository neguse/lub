// ウィンドウが映す物 (中身)。
using Lub;

namespace LiderCore;

public interface IContent
{
    // 映す絵 (無ければ null)
    TextureRef? Texture();
}

// ゲームの版: 保存した版 (GameApp) と、編集中の版 (DraftGame。保存していない本文で変換する)
public enum GameVersion
{
    Saved,
    Draft,
}

// ゲームの絵
public class GameView : IContent
{
    public readonly GameVersion Version;

    public GameView(GameVersion version = GameVersion.Saved)
    {
        Version = version;
    }

    public TextureRef? Texture()
    {
        return Version == GameVersion.Saved ? GameApp.Target : DraftGame.Target;
    }
}
