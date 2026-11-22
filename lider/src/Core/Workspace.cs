// lider state: the list of target files, open Documents, the windows arranged on the code screen and which one has focus, and whether it is taking input.
// Editing keys and characters go to the editor view of the focused window (the editor keymap does editing, selection, undo, and search).
using System;
using System.Collections.Generic;
using Lub;
using LiderEditor;

namespace LiderCore;

// The place before a jump
public class Place
{
    public string Path = "";
    public int Line;
    public int Col;
}

public class Workspace
{
    public readonly string Root;
    // .cs files under Root (in ordinal order of path)
    public List<string> Files = new List<string>();
    // List of image and sound files
    public List<string> AssetFiles = new List<string>();
    // Copies files changed outside into windows that are not being edited (the version formatted after saving on the web)
    public bool FollowDisk = Environment.GetEnvironmentVariable("LIDER_FOLLOW_DISK") == "1";
    // Drafts of unsaved text
    public Drafts? Drafts = Environment.GetEnvironmentVariable("LIDER_DRAFTS") != null ? new Drafts(Environment.GetEnvironmentVariable("LIDER_DRAFTS")!) : null;
    // Image or sound file shown on the panel ("" to show none)
    public string ShownAsset = "";
    // Tick rescans every this many seconds to pick up added and deleted files
    public const float RescanSeconds = 2;
    float sinceScan;
    public readonly Dictionary<string, Document> Docs = new Dictionary<string, Document>();
    // Screen that arranges the code windows: in a single horizontal row (Strip) on a cylinder of radius Space.R around the viewer. The center of the first window is at angle 0
    public readonly Screen CodeScreen = new Screen("code")
    {
        Shape = ScreenShape.Cylinder,
        Anchor = ScreenAnchor.World,
        Position = new Vec3(0, 0, Space.R),
        Layout = new Strip(),
        Movable = true,
    };
    // Screens that can hold code windows (only CodeScreen at first; Lider adds the other screens)
    public List<Screen> Screens = new List<Screen>();
    // Screen where newly opened code windows go (CodeScreen if null)
    public Screen? Home;
    // Newly opened code windows go before this window on Home (at the end if null or not on Home)
    public Window? HomeBefore;

    // Code windows (content is Code). A copy collected in Screens order, and in arrangement order within each screen.
    // To change the order, change Windows of the screen holding the window (ScreenOf)
    public List<Window> Windows
    {
        get
        {
            var found = new List<Window>();
            foreach (var screen in Screens)
                foreach (var window in screen.Windows)
                    if (window.Content is Code)
                        found.Add(window);
            return found;
        }
    }

    // Screen holding window (null if none)
    public Screen? ScreenOf(Window window)
    {
        foreach (var screen in Screens)
            if (screen.Windows.Contains(window))
                return screen;
        return null;
    }

    // The GameView window placed in Space (not in any Screen's arrangement; null if none is placed).
    // A game placed in Space appears as a scene, not as a picture. Only one can be placed
    public Window? InSpace;
    // The Screen to return to from Space, and the position in its arrangement
    Screen? spaceFrom;
    int spaceAt;

    // Removes the GameView window from its Screen's arrangement and places it in Space. Whatever was placed before goes back to its original Screen
    public void PutInSpace(Window window)
    {
        if (window == InSpace || !(window.Content is GameView))
            return;
        TakeFromSpace();
        var screen = ScreenOf(window);
        if (screen == null)
            return;
        spaceFrom = screen;
        spaceAt = screen.Windows.IndexOf(window);
        screen.Windows.Remove(window);
        window.Position = null;
        window.ViewportRect = null;
        window.ContentRect = null;
        InSpace = window;
    }

    // Returns the window placed in Space to the arrangement of its original Screen (to the first Screen if that Screen no longer exists)
    public void TakeFromSpace()
    {
        var window = InSpace;
        if (window == null)
            return;
        InSpace = null;
        var screen = spaceFrom != null && Screens.Contains(spaceFrom) ? spaceFrom : (Screens.Count > 0 ? Screens[0] : null);
        window.ViewportRect = null;
        window.ContentRect = null;
        screen?.Windows.Insert(Math.Min(spaceAt, screen.Windows.Count), window);
    }

    // Moves window to the end of screen's arrangement (removing it from the previous screen). The layout rule of the destination decides its position again
    public void MoveTo(Window window, Screen screen)
    {
        ScreenOf(window)?.Windows.Remove(window);
        window.Position = null;
        window.ViewportRect = null;
        window.ContentRect = null;
        screen.Windows.Add(window);
    }
    // The focused Code window (while focus is on a window with other content, the one that last had focus)
    public Window? FocusWindow;
    // The focused window whose content is not Code (null while focus is on Code)
    Window? other;
    // The window that receives keyboard input (null if no window does)
    public Window? Focused => other ?? FocusWindow;
    // Whether focus is on the game picture window (the keyboard belongs to the game meanwhile)
    public bool OnGame => other != null && other.Content is GameView;

    // Moves focus to window
    public void FocusTo(Window window)
    {
        if (window.Content is Code)
        {
            FocusWindow = window;
            other = null;
        }
        else
            other = window;
    }

    // Returns focus to the Code window (if there is no Code window, no window gets it)
    public void FocusCode()
    {
        other = null;
    }
    public Document? Focus => FocusWindow?.Code.Doc;
    // Focus moves to the window being looked at once it has been looked at for LookSeconds with no typing for QuietSeconds during that time
    // (glancing aside while typing does not move it)
    public const float LookSeconds = 0.35f;
    public const float QuietSeconds = 0.5f;
    Window? looked;
    float lookedFor;
    float sinceKey = QuietSeconds;
    public readonly TraceLog Trace = new TraceLog();
    public bool Shift;
    public bool Ctrl;
    public bool Alt;
    // Keys held down (lub's KeyDown also delivers auto-repeat while held, so this separates the initial press)
    readonly Dictionary<int, bool> held = new Dictionary<int, bool>();

    public Workspace(string root)
    {
        Root = root;
        Screens.Add(CodeScreen);
        Rescan();
    }

    public void Rescan()
    {
        var files = new List<string>();
        AssetFiles = new List<string>();
        Walk(Root, files, 0);
        if (!AssetFiles.Contains(ShownAsset))
            ShownAsset = "";
        // Insertion sort (in ordinal order of path)
        for (var i = 1; i < files.Count; i++)
        {
            var f = files[i];
            var j = i - 1;
            while (j >= 0 && Before(f, files[j]))
            {
                files[j + 1] = files[j];
                j--;
            }
            files[j + 1] = f;
        }
        Files = files;
    }

    // Called every frame while lider is shown
    public void Tick(float dt)
    {
        TakeDeleted();
        if (FollowDisk)
            foreach (var pair in Docs)
                EditorHost.FollowDisk(pair.Value.Id);
        Drafts?.Tick(this, dt);
        sinceKey += dt;
        sinceScan += dt;
        if (sinceScan < RescanSeconds)
            return;
        sinceScan = 0;
        Rescan();
    }

    // Whether a comes before b in ordinal (byte) order
    static bool Before(string a, string b)
    {
        var n = Math.Min(Utf8.Bytes(a), Utf8.Bytes(b));
        for (var i = 0; i < n; i++)
            if (Utf8.At(a, i) != Utf8.At(b, i))
                return Utf8.At(a, i) < Utf8.At(b, i);
        return Utf8.Bytes(a) < Utf8.Bytes(b);
    }

    // Like the dotnet default, does not descend into bin / obj or directories starting with '.'
    void Walk(string dir, List<string> files, int depth)
    {
        if (depth > 8)
            return;
        foreach (var e in Disk.ListDir(dir))
        {
            if (e.Name.StartsWith("."))
                continue;
            var path = dir == "." ? e.Name : dir + "/" + e.Name;
            if (e.IsDir)
            {
                if (e.Name != "bin" && e.Name != "obj")
                    Walk(path, files, depth + 1);
            }
            else if (e.Name.EndsWith(".cs"))
            {
                files.Add(path);
            }
            else if (AssetView.IsAsset(e.Name))
            {
                AssetFiles.Add(path);
            }
        }
    }

    public Document? Open(string path)
    {
        if (Docs.TryGetValue(path, out var open))
            return open;
        var doc = Document.Open(path);
        if (doc == null)
            return null;
        Docs[path] = doc;
        return doc;
    }

    // Path of a file directly under Root (written the same way as Walk)
    public string PathOf(string name)
    {
        return Root == "." ? name : Root + "/" + name;
    }

    // Creates a file with only a type declaration and opens it in a window
    public void Create(string path, string typeName)
    {
        Io.SaveText(path, "public class " + typeName + "\n{\n}\n");
        Rescan();
        FocusOn(path);
    }

    // Asks whether to delete the file shown on the panel or the file of the focused window
    public void AskDeleteFocus()
    {
        if (ShownAsset != "")
            EditorHost.AskDeleteFile(ShownAsset);
        else if (FocusWindow != null)
            EditorHost.AskDelete(FocusWindow.Code.ViewId);
    }

    // Cleans up the windows and Documents of files the user chose to delete
    void TakeDeleted()
    {
        for (var id = EditorHost.TakeDeleted(); id != 0; id = EditorHost.TakeDeleted())
        {
            Rescan();
            string? key = null;
            foreach (var pair in Docs)
                if (pair.Value.Id == id)
                    key = pair.Key;
            if (key == null)
                continue;
            var window = WindowOf(Docs[key]);
            if (window != null)
                CloseTab(window, window.Code.Tabs.IndexOf(Docs[key]));
            Docs.Remove(key);
            EditorHost.Close(id);
            Drafts?.Forget(this, key);
            Rescan();
        }
    }

    // Makes open Documents pick up text changes and saves from the editor.
    // When an unnamed doc is saved with a name, uses that file's path
    public void Sync()
    {
        var named = new List<string>();
        foreach (var pair in Docs)
        {
            pair.Value.Sync();
            if (pair.Value.Path == "" && EditorHost.Path(pair.Value.Id) != "")
                named.Add(pair.Key);
        }
        // The new file is not in the list yet, so rescans before using the path from the list
        if (named.Count > 0)
            Rescan();
        foreach (var key in named)
        {
            var doc = Docs[key];
            var full = EditorHost.Path(doc.Id);
            doc.Path = Resolve(full) ?? full;
            Docs.Remove(key);
            Docs[doc.Path] = doc;
        }
    }

    // lub applied a reload chunk (OnReload)
    public void Reloaded()
    {
        foreach (var doc in Docs.Values)
            doc.Reloaded();
        if (Errors.Count > 0)
        {
            Errors = new List<BuildError>();
            ErrorsVersion++;
            Fixed++;
        }
        if (InitChanged.Count > 0)
        {
            InitChanged = new List<string>();
            ErrorsVersion++;
        }
    }

    // Changes to field initializers in the applied diff that did not reach running instances ("Type.field"). Until the next reload
    public List<string> InitChanged = new List<string>();

    // Notice after lub applies a diff (OnReloadNotes; called after Reloaded)
    public void ReloadNotes(string text)
    {
        const string initChanged = "instance field initializer changed: ";
        foreach (var raw in text.Split('\n'))
            if (raw.StartsWith(initChanged))
                InitChanged.Add(Utf8.Sub(raw, Utf8.Bytes(initChanged)).Trim());
        ErrorsVersion++;
    }

    // Compile errors of the last saved version (until the reload of a fixed version is applied).
    // ErrorsVersion goes up every time Errors or InitChanged changes
    public List<BuildError> Errors = new List<BuildError>();
    public int ErrorsVersion;
    // Number of compile errors received, and number of times the errors were fixed and a reload was applied
    public int Failures;
    public int Fixed;
    // Number of jumps to another place by search or history
    public int Jumps;

    // lub reported a tcs compile error (OnBuildFailed). Error paths are converted to paths in the list
    public void BuildFailed(string text)
    {
        Errors = BuildError.Parse(text);
        foreach (var e in Errors)
            if (e.Path != "")
                e.Path = Resolve(e.Path) ?? e.Path;
        ErrorsVersion++;
        Failures++;
    }

    // The error after the one on the cursor line in the list (the first after the last; the first if none is on the cursor line). Errors in files not in the list are skipped
    public BuildError? NextError()
    {
        var places = new List<BuildError>();
        foreach (var e in Errors)
            if (e.Line >= 0 && Files.Contains(e.Path))
                places.Add(e);
        if (places.Count == 0)
            return null;
        var here = Here();
        for (var i = 0; i < places.Count; i++)
            if (here != null && places[i].Path == here.Path && places[i].Line == here.Line)
                return places[(i + 1) % places.Count];
        return places[0];
    }

    // Errors of the file at path
    public List<BuildError> ErrorsIn(string path)
    {
        var found = new List<BuildError>();
        foreach (var e in Errors)
            if (e.Path == path)
                found.Add(e);
        return found;
    }

    // Shows the tab of path and moves focus to it (if there is no tab, opens it and adds it to the tabs of the focused window)
    public void FocusOn(string path)
    {
        var doc = Open(path);
        if (doc != null)
            Show(doc);
    }

    // Shows the tab of doc and moves focus to it (if there is no tab, adds it to the right of the current tab of the focused window; creates a window if there is none)
    public void Show(Document doc)
    {
        var window = WindowOf(doc);
        if (window != null)
        {
            window.Code.Tab = window.Code.Tabs.IndexOf(doc);
            FocusWindow = window;
            return;
        }
        if (FocusWindow == null)
        {
            var home = Home ?? CodeScreen;
            var before = HomeBefore != null ? home.Windows.IndexOf(HomeBefore) : -1;
            AddWindow(new Window(new Code(doc)), home, before >= 0 ? before : home.Windows.Count);
            return;
        }
        FocusWindow.Code.Tabs.Insert(FocusWindow.Code.Tab + 1, doc);
        FocusWindow.Code.Tab++;
    }

    // Adds the window at position at in screen's arrangement and moves focus to it
    void AddWindow(Window window, Screen screen, int at)
    {
        screen.Windows.Insert(at, window);
        FocusWindow = window;
    }

    // In the focused window, shows the tab by steps away (wrapping from one end to the other)
    public void NextTab(int by)
    {
        var window = FocusWindow;
        if (window != null)
            window.Code.Tab = (window.Code.Tab + by + window.Code.Tabs.Count) % window.Code.Tabs.Count;
    }

    // Moves the current tab of the focused window to the neighboring window on the by side (1 or -1) (creates a new window at the end).
    // Closes a window that becomes empty
    public void MoveTab(int by)
    {
        var from = FocusWindow;
        var screen = from != null ? ScreenOf(from) : null;
        if (from == null || screen == null)
            return;
        // Arrangement of the code windows within the same screen
        var row = CodeWindows(screen);
        if (from.Code.Tabs.Count == 1 && (by < 0 ? row.IndexOf(from) == 0 : row.IndexOf(from) == row.Count - 1))
            return;
        var doc = from.Code.Doc;
        var at = row.IndexOf(from) + by;
        RemoveTab(from, from.Code.Tab);
        if (at < 0 || at >= row.Count)
        {
            AddWindow(new Window(new Code(doc)), screen, at < 0 ? screen.Windows.IndexOf(row[0]) : screen.Windows.IndexOf(row[row.Count - 1]) + 1);
            if (from.Code.Tabs.Count == 0)
                screen.Windows.Remove(from);
            return;
        }
        var to = row[at];
        if (from.Code.Tabs.Count == 0)
            screen.Windows.Remove(from);
        to.Code.Tabs.Insert(to.Code.Tab + 1, doc);
        to.Code.Tab++;
        FocusWindow = to;
    }

    // Closes the current tab of the focused window
    public void CloseTab()
    {
        if (FocusWindow != null)
            CloseTab(FocusWindow, FocusWindow.Code.Tab);
    }

    // Closes tab i of the window (Doc keeps the view, so unsaved text remains). Also closes the window if it is the last tab
    public void CloseTab(Window window, int i)
    {
        if (i < 0 || i >= window.Code.Tabs.Count)
            return;
        if (window.Code.Tabs.Count == 1)
        {
            Close(window);
            return;
        }
        RemoveTab(window, i);
    }

    // Removes the tab (if it is the current tab, shows the one to its right, or to its left if there is none)
    static void RemoveTab(Window window, int i)
    {
        window.Code.Tabs.RemoveAt(i);
        if (window.Code.Tab > i || window.Code.Tab == window.Code.Tabs.Count)
            window.Code.Tab = Math.Max(0, window.Code.Tab - 1);
    }

    // Places before jumps (newest last)
    public readonly List<Place> History = new List<Place>();
    const int MaxHistory = 100;

    // Cursor position of the focused window
    public Place? Here()
    {
        var doc = Focus;
        return doc == null || doc.Path == "" ? null : new Place { Path = doc.Path, Line = doc.Line, Col = doc.Col };
    }

    // Remembers the place before a jump
    public void Remember(Place? place)
    {
        if (place == null)
            return;
        if (History.Count == MaxHistory)
            History.RemoveAt(0);
        History.Add(place);
    }

    // Goes back to a remembered place (reopens the window if it was closed)
    public void Back()
    {
        if (History.Count == 0)
            return;
        var place = History[History.Count - 1];
        History.RemoveAt(History.Count - 1);
        FocusOn(place.Path);
        if (Focus == null)
            return;
        Focus.MoveTo(place.Line, place.Col);
        Jumps++;
        EditorHost.ScrollToLine(Focus.Id, Math.Max(1, place.Line + 1 - ContextLines));
    }

    // Number of lines shown above the destination line of a jump
    public const int ContextLines = 5;

    // Arranges the windows (by the layout rule of the code screen).
    // The grabbed window (held) keeps following the pointed position, and the order swaps when it passes the center of a neighboring window
    public void Arrange(Window? held)
    {
        foreach (var screen in Screens)
        {
            screen.Held = held != null && screen.Windows.Contains(held) ? held : null;
            screen.Arrange();
        }
    }

    // Code windows on screen (in arrangement order)
    static List<Window> CodeWindows(Screen screen)
    {
        var found = new List<Window>();
        foreach (var window in screen.Windows)
            if (window.Content is Code)
                found.Add(window);
        return found;
    }

    // Moves focus to the window by steps away (stops at the ends)
    public void FocusBy(int by)
    {
        if (FocusWindow == null)
            return;
        var at = Windows.IndexOf(FocusWindow) + by;
        if (at >= 0 && at < Windows.Count)
            FocusWindow = Windows[at];
    }

    // Moves the focused window by steps within the arrangement (stops at the ends)
    public void MoveFocus(int by)
    {
        var screen = FocusWindow != null ? ScreenOf(FocusWindow) : null;
        if (FocusWindow == null || screen == null)
            return;
        // Swaps with the neighboring code window within the same screen
        var row = CodeWindows(screen);
        var at = row.IndexOf(FocusWindow);
        if (at + by < 0 || at + by >= row.Count)
            return;
        var a = screen.Windows.IndexOf(FocusWindow);
        var b = screen.Windows.IndexOf(row[at + by]);
        screen.Windows[a] = row[at + by];
        screen.Windows[b] = FocusWindow;
    }

    // Moves focus to the n-th window from the left (from 0)
    public void FocusNth(int n)
    {
        if (n >= 0 && n < Windows.Count)
            FocusWindow = Windows[n];
    }

    // Closes the window with all its tabs (Doc keeps the view, so unsaved text remains).
    // If the window had focus, moves focus to the one to its right (or to its left if there is none)
    public void Close(Window window)
    {
        var at = Windows.IndexOf(window);
        if (at < 0)
            return;
        ScreenOf(window)!.Windows.Remove(window);
        var rest = Windows;
        if (window == FocusWindow)
            FocusWindow = rest.Count > 0 ? rest[Math.Min(at, rest.Count - 1)] : null;
    }

    // Name of the current tab shown in the window title (path from Root and the unsaved mark)
    public string Title(Window window)
    {
        return TabTitle(window.Code.Doc);
    }

    // Names of the tabs listed in the window title
    public List<string> TabTitles(Window window)
    {
        var titles = new List<string>();
        foreach (var doc in window.Code.Tabs)
            titles.Add(TabTitle(doc));
        return titles;
    }

    string TabTitle(Document doc)
    {
        var path = doc.Path == "" ? "untitled" : Relative(doc.Path);
        return EditorHost.Dirty(doc.Id) ? path + " *" : path;
    }

    // Path from Root
    public string Relative(string path)
    {
        return path.StartsWith(Root + "/") ? Utf8.Sub(path, Utf8.Bytes(Root) + 1) : path;
    }

    // Window holding the tab of doc
    public Window? WindowOf(Document doc)
    {
        foreach (var w in Windows)
            if (w.Code.Tabs.Contains(doc))
                return w;
        return null;
    }

    // Passes the window currently being looked at (gaze or pointed position; null if none) every frame
    public void Look(Window? window, float dt)
    {
        if (window != looked)
        {
            looked = window;
            lookedFor = 0;
            return;
        }
        lookedFor += dt;
        if (window != null && window != FocusWindow && lookedFor >= LookSeconds && sinceKey >= QuietSeconds)
            FocusWindow = window;
    }

    // The file in the list that is the same as path (chosen by TraceLog.Match)
    public string? Resolve(string path)
    {
        return TraceLog.Match(path, Files);
    }

    // Remembers key presses and releases; true for a KeyDown (repeat) that arrived while the key was held
    public bool Repeated(int key, bool down)
    {
        var was = held.TryGetValue(key, out var h) && h;
        held[key] = down;
        return down && was;
    }

    // Receives the pressed modifier keys (EventData.Mods) and passes the changes to the editor.
    // Passes them even while not taking input (hiding while a key is held and then releasing it would leave it held)
    public void SetMods(int mods)
    {
        Shift = SetMod(Shift, (mods & Keys.ModShift) != 0, Keys.LeftShift);
        Ctrl = SetMod(Ctrl, (mods & Keys.ModCtrl) != 0, Keys.LeftCtrl);
        Alt = SetMod(Alt, (mods & Keys.ModAlt) != 0, Keys.LeftAlt);
    }

    static bool SetMod(bool was, bool now, int key)
    {
        if (was != now && Document.Booted)
            EditorHost.Key(key, now);
        return now;
    }

    // Types as a key in the US layout
    public bool Key(int key, bool down)
    {
        return Input(key, down, Keys.UsChar(key, Shift), Keys.UsChar(key, false));
    }

    // Passes key to the editor view of the focused window, and also passes the character for printable keys (not while Ctrl is held).
    // typed and plain are the EventData values of the same names.
    // Keys become commands through the table (Keymap), and characters that did not become commands go into the focused Code.
    // true if the key was handled
    public bool Input(int key, bool down, int typed, int plain)
    {
        // Modifier key state comes through SetMods (lub knows which physical keys are modifier keys)
        if (key == Keys.LeftShift || key == Keys.RightShift || key == Keys.LeftCtrl || key == Keys.RightCtrl
            || key == Keys.LeftAlt || key == Keys.RightAlt)
            return true;
        if (down && key == Keys.Escape && ShownAsset != "" && !(Document.Booted && EditorHost.Modal()))
        {
            ShownAsset = "";
            return true;
        }
        if (!Document.Booted)
            return false;
        // Keys become commands through the table. While focus is not on Code, removes the editor's focus so the commands inside the editor do not take effect
        var doc = other == null ? Focus : null;
        if (down)
            sinceKey = 0;
        if (doc != null)
            EditorHost.Focus(FocusWindow!.Code.ViewId);
        else
            EditorHost.Blur();
        EditorHost.KeyNamed(key, down, plain);
        if (doc != null && down && !Ctrl && typed != 0)
            EditorHost.Text(Utf8.Encode(typed));
        doc?.Sync();
        TakeOpened();
        return true;
    }

    // Turns docs opened by editor commands into tabs and moves focus to them.
    // For a file that already has a tab, closes the opened view and shows that tab
    void TakeOpened()
    {
        for (var id = EditorHost.TakeOpened(); id != 0; id = EditorHost.TakeOpened())
        {
            var full = EditorHost.Path(id);
            var path = full == "" ? "" : (Resolve(full) ?? full);
            if (path != "" && Docs.ContainsKey(path))
            {
                EditorHost.Close(id);
                FocusOn(path);
                continue;
            }
            var doc = Document.Adopt(path, id);
            Docs[path != "" ? path : "untitled:" + id] = doc;
            Show(doc);
        }
    }
}
