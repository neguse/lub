// What a window shows (its content).
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public interface IContent
{
    // The picture to show (null if none)
    TextureRef? Texture();
}

// Code: a row of tabs with open files. Draws the text of the shown tab into the window (PanelView; it has no picture to copy to the display)
public class Code : IContent
{
    // Tabs (from the left) and the shown tab
    public readonly List<Document> Tabs = new List<Document>();
    public int Tab;

    public Code(Document doc)
    {
        Tabs.Add(doc);
    }

    public Document Doc => Tabs[Tab];
    // Editor view of the shown tab
    public int ViewId => Doc.Id;

    public TextureRef? Texture()
    {
        return null;
    }
}

// The game's value tree and the load list (a picture drawn by InspectView)
public class Inspector : IContent
{
    public readonly InspectView View;

    public Inspector(InspectView view)
    {
        View = view;
    }

    public TextureRef? Texture()
    {
        // Copies only pictures declared in the previous frame (nothing is declared while lider is hidden)
        return View.DeclaredAt == DeclareGap.Frame ? View.Texture : null;
    }
}

// A board showing the code next to the version the game is running (drawn by PanelView; it has no picture to copy to the display). It becomes a child of the code window
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

// Control hints (drawn by HintCard; it has no picture to copy to the display)
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

// Image and sound files (drawn by AssetView; it has no picture to copy to the display)
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

// Game versions: the saved version (GameApp) and the version being edited (DraftGame; compiled from the unsaved text)
public enum GameVersion
{
    Saved,
    Draft,
}

// The game picture
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
