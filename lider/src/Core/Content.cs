// ウィンドウが映す物 (中身)。
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public interface IContent
{
    // 映す絵 (無ければ null)
    TextureRef? Texture();
}

// コード: file を開いたタブの並び。出しているタブの本文を、ウィンドウに描く (PanelView。画面に写す絵は持たない)
public class Code : IContent
{
    // タブ (左から) と、出しているタブ
    public readonly List<Document> Tabs = new List<Document>();
    public int Tab;

    public Code(Document doc)
    {
        Tabs.Add(doc);
    }

    public Document Doc => Tabs[Tab];
    // 出しているタブの エディタの view
    public int ViewId => Doc.Id;

    public TextureRef? Texture()
    {
        return null;
    }
}

// ゲームの値の木と、負荷の一覧 (InspectView が描いた絵)
public class Inspector : IContent
{
    public readonly InspectView View;

    public Inspector(InspectView view)
    {
        View = view;
    }

    public TextureRef? Texture()
    {
        // 前の frame に宣言した絵だけを写す (lider を隠している間は宣言しない)
        return View.DeclaredAt == DeclareGap.Frame ? View.Texture : null;
    }
}

// コードの、ゲームの動いている版を左右に並べる板 (PanelView が描く。画面に写す絵は持たない)。コードのウィンドウの子になる
public class Diff : IContent
{
    public readonly Code Code;

    public Diff(Code code)
    {
        Code = code;
    }

    public TextureRef? Texture()
    {
        return null;
    }
}

// 操作の案内 (HintCard が描く。画面に写す絵は持たない)
public class Hints : IContent
{
    public readonly HintCard Card;

    public Hints(HintCard card)
    {
        Card = card;
    }

    public TextureRef? Texture()
    {
        return null;
    }
}

// 画像と音の file (AssetView が描く。画面に写す絵は持たない)
public class Asset : IContent
{
    public readonly AssetView View;

    public Asset(AssetView view)
    {
        View = view;
    }

    public TextureRef? Texture()
    {
        return null;
    }
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
