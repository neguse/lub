// The lider entry point.
// Started with `lub Lider.csproj`; it opens and runs the LIDER_GAME game in a separate VM (GameApp). It shows the game image on screen and draws the code space on top of it.
// Arrangements (which windows go on which screen, by which rule) can be recorded and restored by name (Arrangement).
// Two are built in: the game image on the left with the code panels on the right, and the code panels overlaid on the game image; at startup the side-by-side one is used. Alt+L switches to the next arrangement.
// The keyboard goes to the focused window, and the mouse goes to the window it points at.
// F1 moves focus between the game image and Code (while focus is on the game, the panels stay in place, faded). Focus also moves to a window you press.
// At startup only the game image is shown and focus is on the game (the command screen:hide switches to the game image only and back).
// Operations are named commands; key bindings live in Keymap, and commands can also be called by name from the Ctrl+P search.
// Key bindings can be changed with the command keys:set (pick a command, then the next key pressed is bound to it). Changes are written to a per-user file. The command keys:reset restores the defaults.
// Left click on a panel places the cursor. While the game is paused, left click on empty space opens the code that drew the game object, and middle click points at the game object even if code is in front of it.
// Panels are laid out in one horizontal row. In non-XR, right-drag on a panel reorders it (the wheel while dragging resizes it), and right-drag outside the panels moves the row.
// While Alt is held, screen outlines and name tags are shown. A movable screen is moved along its surface by right-dragging its tag, and nearer or farther with the wheel while dragging (the command screen:anchor switches between fixing it to the view and placing it in the world).
// Alt + arrow moves to the adjacent window, Alt + Shift + arrow reorders, and Alt + number moves to the panel with that number. In non-XR the view slides until the focused panel is visible.
// env:
//   LIDER_GAME    Game to open. A .csproj or a directory (the .cs files under it; web) is compiled by lider (GameBuild); a .tcsb is opened as is
//   LIDER_CS_LIB  Directory of lub's cs-lib, compiled together when LIDER_GAME is a directory
//   LIDER_ENTRY   Entry class when LIDER_GAME is a directory (if missing, the static class that has OnFrame)
//   LIDER_ROOT    Directory to search for the .cs files to lay out (default: the directory of the game's .csproj, or "." if LIDER_GAME is missing)
//   LIDER_SIZE    Screen size ("widthxheight")
//   LIDER_HOME    lider directory (fonts are read from data/; default ".")
//   LIDER_ACTIVE  If 1, all screens are shown from startup and focus is put on Code
//   LIDER_OPEN    Path of the file to zoom into at startup
//   LIDER_REWIND  Number of frames to record (default 600; 0 disables recording)
// Time: F5 pauses / resumes the game; while paused, F6 steps back 1 frame and F7 steps forward 1 frame. Shift+F5 recreates the game from the start.
// Ctrl+E jumps through the compile error lines in order.
// Alt+D shows the saved version and the draft version (compiled from the unsaved text) of the game side by side. Alt+D again goes back.
// While they are side by side, input goes to both games, and the command game:copy-to-draft copies the current state of the saved version into the draft version (DraftGame).
using System;
using System.Collections.Generic;
using Lub;
using Lubx;
using LiderCore;
using LiderEditor;

public static class Lider
{
    public static Workspace? Work;
    // Number of times OnReload was called (a sign that a reload chunk was applied)
    public static int Reloads;
    static MonoText? text;
    // Glyphs for the panel body text (density → glyphs). Same column width and line height as text
    static readonly Dictionary<int, MonoText> panelTexts = new Dictionary<int, MonoText>();
    static readonly XrAnchor anchor = new XrAnchor();
    static readonly float[] eyeVp = new float[16];
    // Horizontal position of the non-XR view (x on the plane). The target, and the focused panel at the time the target was set
    static readonly Spring viewX = new Spring(0);
    static float viewTarget;
    static Window? followed;
    // In non-XR, the screen area where the inspector and the focused panel are placed (px, top left is 0,0; LIDER_AREA="x,y,w,h").
    // If AreaW is 0, no area is set (panels have the default size and the view follows the focused panel)
    public static float AreaX;
    public static float AreaY;
    public static float AreaW;
    public static float AreaH;
    // Fraction of the area width used for the inspector
    const float AreaBoard = 0.34f;
    // When an area is set, the height aligned with the middle of the area. The middle of the height including the panel title
    static float AreaCenterY => AreaW > 0 ? PanelView.TitleH * Space.FlatScale * 0.5f : 0;
    static float followedAngle;
    static float followedScale;
    static float followedBoard;
    static float followedOld;
    // Vertical field of view (degrees) outside VR.
    // Narrowing it with the wheel zooms in for reading
    static float fov = Space.FlatFov;
    static Mat4 lastVp = new Mat4();
    // Whether lastVp has been set (whether lider's elements have been drawn at least once)
    static bool cameraSet;
    static int viewportW = 1;
    static int viewportH = 1;
    static readonly Dictionary<string, PanelView> panels = new Dictionary<string, PanelView>();
    // Head direction in VR, and the left eye passed to Gfx.Pick
    static Ray gaze = new Ray(new Vec3(0, 0, 0), new Vec3(0, 0, 1));
    static XrView? lastEye;
    // Controllers: point with the right hand's aim, click with trigger, grab with grip (spread with both hands), scroll with the right stick, and step frames with the left stick while paused
    static readonly Grab grab = new Grab();
    // Per hand (left 0, right 1): whether the trigger is pulled, the surface it points at, and the position on that surface
    static readonly bool[] triggerDown = new bool[2];
    static readonly List<Screen?> aimScreen = new List<Screen?> { null, null };
    static readonly List<Vec2> aimAt = new List<Vec2> { new Vec2(0, 0), new Vec2(0, 0) };
    // The hand that is pressing (-1 if none). Only one hand can press at a time
    static int activeHand = -1;
    // Panel where the left button or trigger was pressed (until release, selects up to the pointed position)
    static Window? pressed;
    static bool rightGrip;
    static bool leftGrip;
    static float stepWait;
    static CylinderPoint? pointer;
    // Interaction feedback: panel visual motion, particles, and the light that flows when focus moves
    static readonly Dictionary<Window, WindowMotion> motions = new Dictionary<Window, WindowMotion>();
    static readonly Particles particles = new Particles();
    static readonly List<Ring> rings = new List<Ring>();
    // Strength of sound and effects (command juice:cycle: 2 everything, 1 no sound, 0 also tones down effects)
    public static int Juice = 2;
    // Full-screen flash (decreases from 1 to 0; dark first, then bright)
    public static float Flash;
    public static float LastLandAt = -100;
    static int seenFixed;
    static int seenJumps;
    // Seconds during which effects are shortened while saving repeatedly
    const float RepeatSeconds = 2;
    static Window? shownFocus;
    static float cometFrom;
    static float cometTo;
    static float cometY;
    static float comet = 1;
    // Panel whose order or size was changed with Alt, and seconds to keep it lifted
    static Window? keyMoved;
    static float keyLift;
    // Coldness of the space while paused (0 to 1), and the position of the dot on the bar of recorded frames
    static float cold;
    public static float Cold => cold;
    static readonly Spring timeline = Stiff();
    static bool timelineShown;
    // Motion of typed characters settling into the surface
    public static readonly List<Landing> Landings = new List<Landing>();
    public static int ParticleCount => particles.Live.Count;
    // While pointing at a game object and waiting for the Gfx.Pick result
    static bool pickPending;
    static float pickX;
    static float pickY;
    // Whether the inspected object is only pointed at rather than clicked
    static bool hoverPick;
    static float hoverX;
    static float hoverY;
    static float hoverStill;
    // Code that drew the pointed game object (innermost first)
    public static List<string> What = new List<string>();
    // Number of MainTex pixels per 1 unit of screen coordinates (CSS px on web)
    public static float PixelRatio = float.Parse(Environment.GetEnvironmentVariable("LIDER_PIXEL_RATIO") ?? "1");
    // Inspector (Alt+I shows it and moves focus to it, or hides it if it has focus; the left hand's X also toggles it). Its content is the Inspector window, placed on the same screen as the code panels.
    // In the overlay arrangement it goes at the far left of the code screen; in the side-by-side arrangement, at the end of the backdrop screen
    static readonly InspectView inspect = new InspectView();
    public static InspectView Inspector => inspect;
    static Window inspectWindow = new Window(new Inspector(inspect));
    public static Window InspectWindow => inspectWindow;
    static bool TreeVisible => inspect.Shown && !ZenShown && inspectWindow.Position != null;
    // Widens panels to the screen width, hides the left board, and dims the game. Not applied while focus is on the game (so the game can be played)
    public static bool Zen;
    static bool ZenShown => Zen && Work != null && !Work.OnGame;
    // Screens placed in space (the game image at the back, and the cylinder of code panels).
    // Flat screens fixed to the view are drawn first by ScreenView, and the code panels and boards are drawn in front of them by DrawScene
    public static readonly List<Screen> Screens = new List<Screen>();
    // On-screen keyboard (toggled with the command keyboard:toggle and the left hand's Y). Its content is the Osk window, first shown on the screen low in front (desk)
    static readonly Osk osk = new Osk();
    public static Osk Keyboard => osk;
    static readonly OskView oskView = new OskView();
    static Window oskWindow = new Window(osk);
    public static Window KeyboardWindow => oskWindow;
    static Screen desk = new Screen("desk");
    // Scale of the on-screen keyboard (1 means the same pixel size as panel body text)
    const float OskScale = 0.75f;
    static bool oskButton;
    // Backmost screen: exactly covers the field of view and shows the game image
    static Screen backdrop = new Screen("backdrop");
    // Window showing the saved version of the game
    static Window gameWindow = new Window(new GameView());
    // Window showing the draft version of the game (only while side by side)
    static Window? draftWindow;
    const float BackdropDistance = 10;
    // Opacity of what lider draws (moves toward GameOpacity while focus is on the game)
    public static float Opacity = 1;
    const float GameOpacity = 0.35f;
    static bool inspectButton;
    // Radius of the boards shown in front (the running-version board, the band when an area is set)
    const float InspectR = Space.R - 0.12f;
    // Non-XR opens panels large, so the inspector text is made about the same size
    static float InspectScale => Space.Flat ? 1.4f : 1;
    // Hints for the operations that currently apply to the pointed-at thing
    static readonly HintCard hintCard = new HintCard();
    // Hint window and the screen it goes on (a flat screen fixed to the view; stacked in the bottom right corner)
    static Window hintWindow = new Window(new Hints(hintCard));
    static Screen hud = new Screen("hud");
    public static Window HintWindow => hintWindow;
    // Search for files and declarations (Ctrl+P). Angle on the cylinder where the board goes, and the height of its top edge
    public static readonly Search Search = new Search();
    static readonly SearchView searchView = new SearchView();
    public static readonly AssetView AssetBoard = new AssetView();
    // Current values shown on field declaration lines
    public static readonly FieldValues Fields = new FieldValues();
    // Per-line load (shown in the panel's line column or tag, and in the load list on the inspector board)
    public static readonly LoadProfile Costs = new LoadProfile();

    static int fieldFrames;
    // Interval for reloading declarations
    const int ScanFrames = 15;
    // Search and asset windows and the screen they go on (a flat screen fixed to the view; stacked downward from the middle of the top edge)
    static Window searchWindow = new Window(Search);
    static Window assetWindow = new Window(new Asset(AssetBoard));
    static Screen palette = new Screen("palette");
    public static Window SearchWindow => searchWindow;

    public static void OnInit()
    {
        var size = (Environment.GetEnvironmentVariable("LIDER_SIZE") ?? "").Split('x');
        if (size.Length == 2 && int.TryParse(size[0], out var width) && int.TryParse(size[1], out var height))
            App.Config(new ConfigOpts { Width = width, Height = height });
        GameApp.Open(Environment.GetEnvironmentVariable("LIDER_GAME") ?? "");
        backdrop = new Screen("backdrop") { Position = new Vec3(0, 0, BackdropDistance) };
        draftWindow = null;
        DraftGame.Close();
        gameWindow = new Window(new GameView());
        backdrop.Windows.Add(gameWindow);
        Screens.Clear();
        Screens.Add(backdrop);
        inspectWindow = new Window(new Inspector(inspect));
        var area = (Environment.GetEnvironmentVariable("LIDER_AREA") ?? "").Split(',');
        if (area.Length == 4 && float.TryParse(area[0], out var ax) && float.TryParse(area[1], out var ay)
            && float.TryParse(area[2], out var aw) && float.TryParse(area[3], out var ah))
            SetArea(ax, ay, aw, ah);
        // Show the difference between the text and the running version inside the panel (inline) from the start (can be switched with the command code:diff-side)
        if (Environment.GetEnvironmentVariable("LIDER_DIFF") == "inline")
            PanelView.SideBySide = false;
        // Record from startup, even when lider is not shown, so it can go back to frames before pausing.
        // While recording, the whole game state (statics and the objects reachable from them) is copied before each game step, so every frame is heavier by that much.
        // LIDER_REWIND=0 disables recording
        Record(Environment.GetEnvironmentVariable("LIDER_REWIND"));
        Work = new Workspace(Environment.GetEnvironmentVariable("LIDER_ROOT") ?? GameDir());
        Screens.Add(Work.CodeScreen);
        palette = new Screen("palette") { Position = new Vec3(0, 0, Space.R), Layout = new Stack { AlignX = 0, FromTop = true } };
        searchWindow = new Window(Search);
        assetWindow = new Window(new Asset(AssetBoard));
        Search.Window = searchWindow;
        Screens.Add(palette);
        hud = new Screen("hud") { Position = new Vec3(0, 0, Space.R), Layout = new Stack() };
        hintWindow = new Window(new Hints(hintCard));
        hud.Windows.Add(hintWindow);
        Screens.Add(hud);
        // Surface low in front where the on-screen keyboard goes (placed in the world)
        desk = new Screen("desk") { Anchor = ScreenAnchor.World, Position = new Vec3(0, -0.3f, 1.1f), Size = new Vec2(1.4f, 0.42f), Layout = new Stack { AlignX = 0 } };
        oskWindow = new Window(osk);
        osk.Shown = false;
        osk.Mods = 0;
        Screens.Add(desk);
        // Keep only one list of screens
        Work.Screens = Screens;
        backdrop.Layout = new Even();
        // At startup show only the game and put focus on the game (with LIDER_ACTIVE=1, show everything from the start and put focus on Code)
        gameHeld = false;
        cameraSet = false;
        SetHidden(true);
        if (Environment.GetEnvironmentVariable("LIDER_ACTIVE") == "1")
            FocusCode();
        RegisterCommands();
        BuiltinArrangements(Work);
        // At startup use the side-by-side arrangement
        Arrange(SideBySide);
        Work.Drafts?.Restore(Work);
        // If lider's VM was recreated (a change to lider's own static initializers), restore the panels from before the recreation
        if (Session.Restore(Work, Sys.Seconds()))
            return;
        var open = Environment.GetEnvironmentVariable("LIDER_OPEN");
        if (open != null)
            Work.FocusOn(open);
    }

    // Called by lub before it recreates the VM and before it exits
    public static void OnQuit()
    {
        if (Work != null)
            Session.Save(Work, Sys.Seconds());
    }

    // Directory of the game (LIDER_GAME) ("." if LIDER_GAME is missing)
    static string GameDir()
    {
        var module = GameApp.Project;
        for (var i = Utf8.Bytes(module) - 1; i > 0; i--)
            if (Utf8.At(module, i) == '/')
                return Utf8.Sub(module, 0, i);
        return ".";
    }

    // The saved version got into the game (a reload was applied, or the game was recreated)
    public static void GameReloaded()
    {
        Reloads++;
        Work?.Reloaded();
    }

    // The applied diff had changes that do not reach the running objects (called after GameReloaded)
    public static void GameReloadNotes(string notes)
    {
        Work?.ReloadNotes(notes);
    }

    // Notices about changed initial values of fields declared in the panel's file that did not reach the running instances.
    // Fields whose declaration is not found go to the focused panel
    public static List<string> InitNotices(Workspace w, Window window)
    {
        var notices = new List<string>();
        foreach (var name in w.InitChanged)
        {
            var item = Search.Find(name);
            if (item == null ? window != w.FocusWindow : item.Path != window.Code.Doc.Path)
                continue;
            notices.Add((item != null ? (item.Line + 1) + ": " : "") + name + " の初期値は動いている物に入りません (Shift+F5 で作り直すと入る)");
        }
        if (DraftGame.Notice != "" && window == w.FocusWindow)
            notices.Add(DraftGame.Notice);
        if (StateNotice != "" && window == w.FocusWindow)
            notices.Add(StateNotice);
        return notices;
    }

    // The saved version has a compile error and did not get into the game
    public static void GameBuildFailed(string errors)
    {
        Work?.BuildFailed(errors);
    }

    // Sets the number of frames to record (the LIDER_REWIND value; 600 if missing or unreadable, 0 disables recording)
    public static void Record(string? frames)
    {
        GameApp.Keep(frames != null && int.TryParse(frames, out var n) ? n : 600);
    }

    // Moves focus to the game image window (the keyboard reaches the game)
    public static void FocusGame()
    {
        if (Work == null)
            return;
        Work.FocusTo(gameWindow);
        if (Search.Shown)
            Search.Cancel(Work);
    }

    // Moves focus to the Code window (hidden screens are shown)
    public static void FocusCode()
    {
        if (Work == null)
            return;
        SetHidden(false);
        Work.FocusCode();
    }

    // Whether only the game image is shown
    public static bool Hidden => Work != null && hidden;
    static bool hidden;

    // Shows only the game image / restores. When switching to the game image only, moves focus to the game
    public static void SetHidden(bool hidden)
    {
        if (Work == null)
            return;
        Lider.hidden = hidden;
        PutAway();
        if (hidden)
            FocusGame();
    }

    // While hidden, show only the game image: screens without the game image are not shown, and on screens with the game image, other windows are put away.
    // Decided again every frame so it also applies to windows added while hidden
    static void PutAway()
    {
        foreach (var screen in Screens)
        {
            var game = HasGame(screen);
            screen.Shown = !hidden || game;
            foreach (var window in screen.Windows)
                window.Away = hidden && game && !(window.Content is GameView);
        }
    }

    static bool HasGame(Screen screen)
    {
        foreach (var window in screen.Windows)
            if (window.Content is GameView)
                return true;
        return false;
    }

    // Whether lider's drawing (windows other than the game image, search, hints) is shown
    static bool LiderShown(Workspace w)
    {
        return !hidden;
    }

    // Whether a mouse button was pressed on the game image and not released yet (until release, mouse input goes to the game)
    static bool gameHeld;

    // Whether mouse input at screen (x, y) belongs to the game.
    // While the game runs, it goes to the game unless it points at a lider element. While paused, lider takes it over the game image too (to inspect the code that drew it)
    static bool MouseForGame(Workspace w, float x, float y)
    {
        if (!LiderShown(w))
            return true;
        if (lastEye != null)
            return w.OnGame;
        if (gameHeld)
            return true;
        if (Paused || pressed != null || sizing != null || grab.Holding || heldScreen != null)
            return false;
        return !OverLider(w, PointerRay(x, y));
    }

    // Whether the ray points at a lider element (a window, its title and edges, the search board)
    static bool OverLider(Workspace w, Ray ray)
    {
        return Search.Shown || OskUv(w, ray) != null || TagAt(w, ray) != null || OnSpaceTag(ray) || InspectAt(ray) != null || TitleAt(w, ray) != null || WindowAt(w, ray) != null
            || (!Zen && EdgeWindow(w, ray) != null);
    }

    public static void OnEvent(EventData e)
    {
        var w = Work;
        if (w == null)
            return;
        if (e.Kind == EventKind.KeyDown || e.Kind == EventKind.KeyUp)
            w.SetMods(e.Mods);
        var repeated = (e.Kind == EventKind.KeyDown || e.Kind == EventKind.KeyUp)
            && w.Repeated(e.Key, e.Kind == EventKind.KeyDown);
        var key = e.Kind == EventKind.KeyDown || e.Kind == EventKind.KeyUp;
        var mouse = e.Kind == EventKind.MouseButtonDown || e.Kind == EventKind.MouseButtonUp
            || e.Kind == EventKind.MouseMotion || e.Kind == EventKind.MouseWheel;
        // The keyboard goes to the focused window, the mouse to the pointed window, and everything else (gamepad etc.) to the game
        if (!key && !mouse)
        {
            SendGame(e);
            return;
        }
        if (key && w.OnGame)
        {
            // While focus is on the game, the only key lider takes is the one that brings focus back
            var name = Document.Booted ? EditorHost.NameOf(e.Key, e.Plain) : null;
            if (name != null && Keymap.Binds(name, "focus:game"))
            {
                if (e.Kind == EventKind.KeyDown && !repeated)
                    CommandTable.Run("focus:game");
                return;
            }
            // Before the editor is started, the table cannot be looked up, so F1 brings focus back
            if (!Document.Booted && e.Key == Keys.F1)
            {
                if (e.Kind == EventKind.KeyDown && !repeated)
                    FocusCode();
                return;
            }
            SendGame(e);
            return;
        }
        if (mouse)
        {
            var x = e.X;
            var y = e.Y;
            if (e.Kind == EventKind.MouseWheel)
                Input.MousePos(out x, out y);
            // Where a press goes is decided by what it points at when it starts
            if (e.Kind == EventKind.MouseButtonDown)
                gameHeld = false;
            var forGame = MouseForGame(w, x, y);
            if (e.Kind == EventKind.MouseButtonDown)
                gameHeld = forGame;
            if (e.Kind == EventKind.MouseButtonUp)
                gameHeld = false;
            if (forGame)
            {
                // Pressing the game image also moves focus to the game
                if (e.Kind == EventKind.MouseButtonDown)
                {
                    // Move focus to the pressed image window (the version lider looks at also becomes that version)
                    PointGames(x, y);
                    if (gamePointed != null && w.Focused != gamePointed)
                        w.FocusTo(gamePointed);
                    else if (!w.OnGame)
                        FocusGame();
                }
                SendGame(e);
                return;
            }
        }
        if (Search.Shown && SearchEvent(w, e))
            return;
        // Keys become commands through the table (Keymap). Keys that do not become commands go into the focused Code
        if (key)
        {
            CommandTable.Repeated = repeated;
            w.Input(e.Key, e.Kind == EventKind.KeyDown, e.Typed, e.Plain);
            CommandTable.Repeated = false;
        }
        else if (e.Kind == EventKind.MouseButtonDown && e.Button == 1)
            ClickAt(e.X, e.Y);
        else if (e.Kind == EventKind.MouseButtonDown && e.Button == 3 && lastEye == null)
        {
            // On a screen's name tag, grab the screen. Otherwise, grab the window pointed at
            if (!GrabScreenAt(w, e.X, e.Y))
                GrabAt(w, e.X, e.Y);
        }
        else if (e.Kind == EventKind.MouseMotion && heldScreen != null)
            DragScreen(PointerRay(e.X, e.Y));
        else if (e.Kind == EventKind.MouseButtonUp && e.Button == 3 && heldScreen != null)
            heldScreen = null;
        else if (e.Kind == EventKind.MouseWheel && heldScreen != null)
            heldScreen.SetDepth(heldScreen.Radius / (float)Math.Pow(1.1f, e.Dy));
        else if (e.Kind == EventKind.MouseMotion && grab.Holding && lastEye == null)
        {
            var at = SurfacePoint(ScreenFor(w, grab.Window!), PointerRay(e.X, e.Y));
            if (at != null)
                grab.Move(at.Value.X, at.Value.Y);
        }
        else if (e.Kind == EventKind.MouseButtonUp && e.Button == 3 && lastEye == null)
            grab.Release();
        else if (e.Kind == EventKind.MouseMotion && sizing != null)
            DragEdge(w, PointerRay(e.X, e.Y));
        else if (e.Kind == EventKind.MouseButtonUp && e.Button == 1 && sizing != null)
            EndEdge();
        else if (e.Kind == EventKind.MouseMotion && pressed != null)
            DragTo(w, lastEye != null ? gaze : PointerRay(e.X, e.Y));
        else if (e.Kind == EventKind.MouseButtonUp && e.Button == 1)
            ReleasePress(w);
        else if (e.Kind == EventKind.MouseButtonDown && e.Button == 2)
            PickAt(e.X, e.Y);
        else if (e.Kind == EventKind.MouseWheel && grab.Holding && lastEye == null)
            grab.Zoom(e.Dy);
        else if (e.Kind == EventKind.MouseWheel)
        {
            // On a panel, scroll; with Ctrl held, change the field of view to zoom in
            Input.MousePos(out var mx, out var my);
            var hit = w.Ctrl || lastEye != null ? null : WindowAt(w, PointerRay(mx, my));
            if (!w.Ctrl && InspectAt(lastEye != null ? gaze : PointerRay(mx, my)) != null)
                inspect.Scroll -= e.Dy * 3;
            else if (hit != null)
                EditorHost.Scroll(hit.Window.Code.ViewId, e.Dy);
            else
                fov = Math.Max(15, Math.Min(90, fov - e.Dy * 4));
        }
    }

    public static void SetJuice(int juice)
    {
        Juice = juice;
        Cues.Muted = juice < 2;
    }

    // Input while search is shown (modifier key presses and releases are also passed to Workspace). Returns true if handled
    static bool SearchEvent(Workspace w, EventData e)
    {
        if (e.Kind == EventKind.MouseButtonDown)
        {
            // Pressing on a board does not reach the panel below it
            var onBoard = lastEye == null && searchWindow.ViewportRect != null && searchWindow.ViewportRect.Contains(e.X, e.Y);
            Search.Cancel(w);
            return onBoard;
        }
        if (e.Kind != EventKind.KeyDown && e.Kind != EventKind.KeyUp)
            return false;
        if (e.Key == Keys.LeftShift || e.Key == Keys.RightShift || e.Key == Keys.LeftCtrl || e.Key == Keys.RightCtrl
            || e.Key == Keys.LeftAlt || e.Key == Keys.RightAlt)
            return false;
        if (e.Kind == EventKind.KeyUp)
            return false;
        // While waiting for a key, bind the next pressed key to the selected command
        if (keyFor != null)
        {
            TakeKey(w, e);
            return true;
        }
        // The key that moves focus to the game works even while search is shown (search is closed)
        var name = Document.Booted ? EditorHost.NameOf(e.Key, e.Plain) : null;
        if (name != null && Keymap.Binds(name, "focus:game"))
            return false;
        if (e.Key == Keys.Escape || (w.Ctrl && e.Key == Keys.P))
            Search.Cancel(w);
        else if (e.Key == Keys.Return)
            Search.Commit(w);
        else if (e.Key == Keys.Up)
            Search.Move(w, -1);
        else if (e.Key == Keys.Down)
            Search.Move(w, 1);
        else if (e.Key == Keys.Backspace)
            Search.Backspace(w);
        else if (!w.Ctrl && e.Typed != 0)
            Search.Type(w, Utf8.Encode(e.Typed));
        return true;
    }

    static float SearchScale => Space.Flat ? Space.FlatScale * 0.8f : 1;

    // Put the search and asset windows on the screen only while shown, size them to the board, and place them by the rule (downward from the middle of the top edge)
    static void PlacePalette(Workspace w)
    {
        Show(palette, searchWindow, Search.Shown, 0);
        Show(palette, assetWindow, w.ShownAsset != "", palette.Windows.Count);
        searchWindow.Size = new Vec2(SearchView.W * SearchScale / ViewScale(palette), searchView.Height(SearchScale) / ViewScale(palette));
        assetWindow.Size = new Vec2(AssetView.W * SearchScale / ViewScale(palette), AssetView.H * SearchScale / ViewScale(palette));
        palette.Arrange();
    }

    // Puts window in screen's list at index at, only while shown
    static void Show(Screen screen, Window window, bool shown, int at)
    {
        var on = screen.Windows.Contains(window);
        if (shown && !on)
            screen.Windows.Insert(Math.Min(at, screen.Windows.Count), window);
        else if (!shown && on)
        {
            screen.Windows.Remove(window);
            window.Position = null;
        }
    }

    // Height of the window's top edge (a board is drawn from the middle of its top edge)
    static float WindowTop(Workspace w, Window window)
    {
        return WindowY(w, window) + window.Size.Y * ViewScale(ScreenFor(w, window)) * 0.5f;
    }

    // Screen outlines and name tags: shown only while Alt is held and while a screen is grabbed.
    // A movable screen moves along its surface when its tag is grabbed with the right button, and nearer or farther with the wheel while held
    static readonly Dictionary<Screen, TitleView> screenTags = new Dictionary<Screen, TitleView>();
    static Screen? heldScreen;
    // The grabbed point (coordinates on the screen surface). While moving, the screen is carried so the pointed point stays on it
    static Vec2 heldAt;
    // Tag text size (1 = same as panel body text), outline line width, and margin outside the outline (meters at radius Space.R)
    const float TagScale = Space.FlatScale;
    const float OutlineW = 0.004f;
    const float OutlinePad = 0.02f;

    public static bool ScreensLit => Work != null && (heldScreen != null || (Work.Alt && !Work.OnGame && LiderShown(Work)));
    public static Screen? HeldScreen => heldScreen;

    // Factor that turns a length on the surface into the same apparent length at radius Space.R
    static float Apparent(Screen screen)
    {
        return OnPlane(screen) ? ViewScale(screen) : 1;
    }

    // Outline of screen (a rectangle on the screen surface: center x and y, width, height).
    // A screen with a size uses that size; a screen sized by its contents uses the rectangle around its shown windows
    public static Vec4 OutlineOf(Screen screen)
    {
        if (OnPlane(screen))
            return new Vec4(0, 0, screen.Size.X, screen.Size.Y);
        var any = false;
        float x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        foreach (var window in screen.Windows)
        {
            if (window.Position == null)
                continue;
            var at = window.Position.Value;
            // A code panel has its title on top of the body text
            var top = at.Y + window.Size.Y * 0.5f + ((window.Content is Code) ? PanelView.TitleH * window.Scale : 0);
            x0 = any ? Math.Min(x0, at.X - window.Size.X * 0.5f) : at.X - window.Size.X * 0.5f;
            x1 = any ? Math.Max(x1, at.X + window.Size.X * 0.5f) : at.X + window.Size.X * 0.5f;
            y0 = any ? Math.Min(y0, at.Y - window.Size.Y * 0.5f) : at.Y - window.Size.Y * 0.5f;
            y1 = any ? Math.Max(y1, top) : top;
            any = true;
        }
        if (!any)
            return new Vec4(0, 0, 0.6f, 0.3f);
        return new Vec4((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, x1 - x0 + 2 * OutlinePad, y1 - y0 + 2 * OutlinePad);
    }

    // Rectangle of screen's name tag (on the screen surface: center x and y, width, height). null if the tag is not shown.
    // Placed inside the top left of the outline. Screens with a size have overlapping outlines, so their tags are laid out horizontally in screen order.
    // Other screens put the tag at the top left of the visible part of the outline, so the tag can be grabbed even if the outline extends off screen
    public static Vec4? TagOf(Screen screen)
    {
        if (!ScreensLit || !screen.Shown || !screenTags.TryGetValue(screen, out var tag))
            return null;
        var k = Apparent(screen);
        var perPx = PanelView.BaseW / PanelView.BasePxW * TagScale / k;
        var w = tag.PxW * perPx;
        var h = PanelView.TitlePx * perPx;
        var box = OutlineOf(screen);
        var left = box.X - box.Z * 0.5f;
        var top = box.Y + box.W * 0.5f;
        var pad = OutlinePad / k;
        if (!OnPlane(screen))
        {
            var corner = lastEye == null ? SurfacePoint(screen, PointerRay(0, 0)) : null;
            if (corner != null)
            {
                left = Math.Min(Math.Max(left, corner.Value.X), box.X + box.Z * 0.5f - w - 2 * pad);
                // Leave the top row of the screen free, since the tags of screens with a size are lined up there
                top = Math.Max(Math.Min(top, corner.Value.Y - h - pad), box.Y - box.W * 0.5f + h + 2 * pad);
            }
            return new Vec4(left + pad + w * 0.5f, top - pad - h * 0.5f, w, h);
        }
        foreach (var other in Screens)
        {
            if (other == screen)
                break;
            if (other.Shown && OnPlane(other) && screenTags.TryGetValue(other, out var before))
                left += before.PxW * perPx + pad;
        }
        return new Vec4(left + pad + w * 0.5f, top - pad - h * 0.5f, w, h);
    }

    // Space tag: while the game is placed in Space, shown to the right of the row of screen name tags. Pressing it returns the game to the window on its original screen.
    // The tag is placed on the surface of the screen of the game image at the back (backdrop)
    static readonly TitleView spaceTag = new TitleView("lider_space_tag");

    // Rectangle of the Space tag (on the backdrop surface: center x and y, width, height). null if not shown
    public static Vec4? SpaceTagOf()
    {
        if (!ScreensLit || Work == null || Work.InSpace == null || !OnPlane(backdrop))
            return null;
        var k = Apparent(backdrop);
        var perPx = PanelView.BaseW / PanelView.BasePxW * TagScale / k;
        var pad = OutlinePad / k;
        var left = -backdrop.Size.X * 0.5f;
        foreach (var screen in Screens)
            if (screen.Shown && OnPlane(screen) && screenTags.TryGetValue(screen, out var tag))
                left += tag.PxW * perPx + pad;
        var w = spaceTag.PxW * perPx;
        var h = PanelView.TitlePx * perPx;
        return new Vec4(left + pad + w * 0.5f, backdrop.Size.Y * 0.5f - pad - h * 0.5f, w, h);
    }

    // Whether ray points at the Space tag
    static bool OnSpaceTag(Ray ray)
    {
        var tag = SpaceTagOf();
        var at = tag != null ? SurfacePoint(backdrop, ray) : null;
        return tag != null && at != null && Math.Abs(at.Value.X - tag.Value.X) <= tag.Value.Z * 0.5f && Math.Abs(at.Value.Y - tag.Value.Y) <= tag.Value.W * 0.5f;
    }

    // Screen whose name tag the ray points at (nearer screens are checked first)
    static Screen? TagAt(Workspace w, Ray ray)
    {
        Screen? found = null;
        foreach (var screen in Screens)
        {
            var tag = TagOf(screen);
            var at = tag != null ? SurfacePoint(screen, ray) : null;
            if (tag == null || at == null)
                continue;
            if (Math.Abs(at.Value.X - tag.Value.X) <= tag.Value.Z * 0.5f && Math.Abs(at.Value.Y - tag.Value.Y) <= tag.Value.W * 0.5f
                && (found == null || screen.Distance < found.Distance))
                found = screen;
        }
        return found;
    }

    // If screen (x, y) is on the name tag of a movable screen, grab that screen
    static bool GrabScreenAt(Workspace w, float x, float y)
    {
        var ray = PointerRay(x, y);
        var screen = TagAt(w, ray);
        var at = screen != null && screen.Movable ? SurfacePoint(screen, ray) : null;
        if (screen == null || at == null)
            return false;
        heldScreen = screen;
        heldAt = at.Value;
        return true;
    }

    // Move the grabbed screen along its surface so the grabbed point comes to where the ray points
    static void DragScreen(Ray ray)
    {
        var at = heldScreen != null ? SurfacePoint(heldScreen, ray) : null;
        if (heldScreen != null && at != null)
            heldScreen.Shift(at.Value.X - heldAt.X, at.Value.Y - heldAt.Y);
    }

    // Draw the tag textures only while shown (call outside the pass to the screen)
    static void PrepareScreenTags(Workspace w, MonoText text)
    {
        if (!ScreensLit)
            return;
        foreach (var screen in Screens)
        {
            if (!screenTags.TryGetValue(screen, out var tag))
            {
                tag = new TitleView("lider_screen_tag_" + screen.Name);
                screenTags[screen] = tag;
            }
            tag.Label = screen.Name;
            // Tags of screens that cannot be moved are shown faded
            tag.Focused = screen.Movable;
            tag.PxW = (int)(text.CellW * (Utf8.Bytes(screen.Name) + 2)) + 8;
            if (screen.Shown)
                tag.Prepare(text);
        }
        if (w.InSpace != null)
        {
            spaceTag.Label = "space: " + GameTitle(w.InSpace) + " ↓";
            spaceTag.Focused = true;
            spaceTag.PxW = (int)(text.CellW * (Utf8.Cells(spaceTag.Label, Utf8.Bytes(spaceTag.Label)) + 2)) + 8;
            spaceTag.Prepare(text);
        }
    }

    // Screen px, in the last drawn frame, of (x, y) on screen's surface
    public static Vec2 SurfacePx(Screen screen, float x, float y)
    {
        var flat = Space.Flat;
        Space.Flat = flat || screen.Shape == ScreenShape.Plane;
        var k = Apparent(screen);
        var p = OnPlane(screen) ? Space.At((screen.Position.X + x) / screen.Position.Z, (screen.Position.Y + y) * k, Space.R) : Space.At(screen.AngleAt(x), y, Space.R);
        Space.Flat = flat;
        return ViewportOn(screen, p);
    }

    // Draw a rectangle on screen's surface (center x and y, width, height) with a color or a texture
    static void SurfaceQuad(Screen screen, Mat4 world, Vec4 rect, Color color, TextureRef? tex)
    {
        var flat = Space.Flat;
        Space.Flat = flat || screen.Shape == ScreenShape.Plane;
        var k = Apparent(screen);
        var angle = OnPlane(screen) ? (screen.Position.X + rect.X) / screen.Position.Z : screen.AngleAt(rect.X);
        var y = OnPlane(screen) ? (screen.Position.Y + rect.Y) * k : rect.Y;
        if (tex != null)
            Quad3d.Curved(VpOf(screen, world), tex, color, angle, y, Space.R, rect.Z * k, rect.W * k, 0, 1);
        else
            Quad3d.CurvedSolid(VpOf(screen, world), color, angle, y, Space.R, rect.Z * k, rect.W * k);
        Space.Flat = flat;
    }

    static void DrawScreenTags(Workspace w, Mat4 world)
    {
        if (!ScreensLit)
            return;
        foreach (var screen in Screens)
        {
            var tag = TagOf(screen);
            if (tag == null)
                continue;
            var box = OutlineOf(screen);
            var line = OutlineW / Apparent(screen);
            var color = screen == heldScreen ? Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.95f)
                : screen.Movable ? Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.55f)
                : Color.Rgb(Theme.Muted.R, Theme.Muted.G, Theme.Muted.B, 0.35f);
            // The outline is drawn inside the rectangle (visible even for a full-screen screen)
            SurfaceQuad(screen, world, new Vec4(box.X, box.Y + (box.W - line) * 0.5f, box.Z, line), color, null);
            SurfaceQuad(screen, world, new Vec4(box.X, box.Y - (box.W - line) * 0.5f, box.Z, line), color, null);
            SurfaceQuad(screen, world, new Vec4(box.X - (box.Z - line) * 0.5f, box.Y, line, box.W), color, null);
            SurfaceQuad(screen, world, new Vec4(box.X + (box.Z - line) * 0.5f, box.Y, line, box.W), color, null);
            SurfaceQuad(screen, world, tag.Value, Theme.Surface, null);
            var texture = screenTags[screen].Texture;
            if (texture != null)
                SurfaceQuad(screen, world, tag.Value, Color.Rgb(1, 1, 1, 1), texture);
        }
        var space = SpaceTagOf();
        if (space != null)
        {
            SurfaceQuad(backdrop, world, space.Value, Theme.Surface, null);
            if (spaceTag.Texture != null)
                SurfaceQuad(backdrop, world, space.Value, Color.Rgb(1, 1, 1, 1), spaceTag.Texture);
        }
    }

    // Grab the window pointed at with the mouse (any window content, on any screen).
    // The game image is grabbed by its title band (the mouse over the image belongs to the game)
    static void GrabAt(Workspace w, float x, float y)
    {
        var ray = PointerRay(x, y);
        var window = TitleAt(w, ray)?.Window;
        if (window == null && InspectAt(ray) != null)
            window = inspectWindow;
        window ??= WindowAt(w, ray)?.Window;
        if (window == null || window.Position == null)
            return;
        var at = SurfacePoint(ScreenFor(w, window), ray);
        if (at == null)
            return;
        grab.Start(window, at.Value.X, at.Value.Y);
        // From the next layout on, the arrangement rule does not move this window
        ScreenFor(w, window).Held = window;
    }

    // Where ray hits screen's surface (coordinates on the screen surface; null if it does not hit)
    static Vec2? SurfacePoint(Screen screen, Ray ray)
    {
        var flat = Space.Flat;
        Space.Flat = flat || screen.Shape == ScreenShape.Plane;
        var at = Space.OnCylinderAt(RayOn(screen, ray), Space.R);
        Space.Flat = flat;
        if (at == null)
            return null;
        // Flat screens fixed to the view are hit at radius Space.R with the same apparent size, so scale back to the screen's distance
        if (OnPlane(screen))
            return new Vec2(at.Angle * screen.Position.Z - screen.Position.X, at.Y / ViewScale(screen) - screen.Position.Y);
        return new Vec2(screen.XAt(at.Angle), at.Y);
    }

    // Closes a window (works for any window content). If it had focus, focus goes back to the Code window
    public static void Close(Workspace w, Window window)
    {
        if (window.Content is Code)
            w.Close(window);
        else if (window == draftWindow)
            ToggleCompare();
        else if (window == searchWindow)
            Search.Cancel(w);
        else if (window == assetWindow)
            w.ShownAsset = "";
        else if (window == inspectWindow)
        {
            inspect.Shown = false;
            if (w.Focused == window)
                w.FocusCode();
        }
    }

    // Closes the focused window (in Code, closes the current tab, and the whole window if it is the last tab)
    public static void CloseFocused(Workspace w)
    {
        var window = w.Focused;
        if (window == null)
            return;
        if (window.Content is Code)
            w.CloseTab();
        else
            Close(w, window);
    }

    // Narrows the inspector to a row. For an instance, also makes the editor's field values show that instance
    public static void EnterScope(InspectRow row)
    {
        inspect.Tree.Enter(row);
        if (row.Id != "")
            Fields.Select(row.Key);
    }

    // Makes the narrowed instance follow where it was found by tracing from statics (FieldValues). Goes back to the class when it can no longer be found
    static void FollowScope()
    {
        var tree = inspect.Tree;
        if (tree.ScopeId == "")
            return;
        var path = Fields.PathOf(tree.ScopeClass, tree.ScopeId);
        if (path == "")
            tree.ScopeTo(1);
        else
            tree.Rebase(path);
    }

    // Instance whose execution marks are drawn (the narrowing ScopeId; the marks are cleared when it changes)
    static string tracedScope = "";

    // Narrows to the instance with id (does nothing if it has not been found by tracing from statics)
    public static void ScopeToInstance(string id)
    {
        var found = Fields.Find(id);
        if (found == null)
            return;
        Fields.Select(found.Path);
        inspect.Tree.EnterInstance(found.Class, id, found.Path);
    }

    // Returns the inspector to the i-th level of the breadcrumbs. When leaving an instance, also clears the editor's instance selection
    public static void ScopeTo(int i)
    {
        var cls = inspect.Tree.ScopeClass;
        var id = inspect.Tree.ScopeId;
        inspect.Tree.ScopeTo(i);
        if (id != "" && inspect.Tree.ScopeId == "" && Fields.IsSelected(cls, id))
            Fields.Toggle(cls, id);
    }

    // The instance marker on a class line in the editor was pressed: select that instance and narrow the inspector to it too (if already selected, deselect it and go back to the class)
    public static void ToggleInstance(string cls, string id)
    {
        Fields.Toggle(cls, id);
        var path = Fields.PathOf(cls, id);
        if (Fields.IsSelected(cls, id) && path != "")
            inspect.Tree.EnterInstance(cls, id, path);
        else if (inspect.Tree.ScopeId == id)
            inspect.Tree.ScopeTo(1);
    }

    // Shows the panel's i-th tab and moves focus to it (a tab in the title was pressed)
    public static void ShowTab(Window window, int i)
    {
        var w = Work;
        if (w == null || i < 0 || i >= window.Code.Tabs.Count)
            return;
        window.Code.Tab = i;
        w.FocusWindow = window;
    }

    // Width changed by Alt+Shift+PageUp / PageDown (px, about 8 columns)
    // Window size changed per key press (meters; apparent length at radius Space.R)
    static float SizeStep => 64 * PanelView.BaseW / PanelView.BasePxW * (Space.Flat ? Space.FlatScale : 1);

    // Registers lider's operations in the table as named commands (key bindings are in Keymap; search calls them by the same names)
    static void RegisterCommands()
    {
        CommandTable.Register("focus:game", "focus をゲームとコードの間で移す", () => With(w =>
        {
            if (w.OnGame)
                FocusCode();
            else
                FocusGame();
        })).OverModal = true;
        CommandTable.Register("screen:hide", "ゲームの絵だけを出す・戻す", () => With(w => SetHidden(!Hidden)));
        CommandTable.Register("screen:anchor", "スクリーンを視点に固定する・ワールドに置く", () =>
        {
            var w = Work;
            var screen = w != null && w.Focused != null ? w.ScreenOf(w.Focused) : null;
            if (screen == null || !screen.Movable)
                return false;
            screen.Anchor = screen.Anchor == ScreenAnchor.View ? ScreenAnchor.World : ScreenAnchor.View;
            return true;
        });
        CommandTable.Register("keys:set", "命令に割り当てる key を変える", () => With(w => Search.Pick(w, "key を変える命令", name =>
        {
            keyFor = name;
            Search.Ask(w, KeyPrompt(name), typed => { });
        })));
        CommandTable.Register("keys:reset", "key の割り当てを既定に戻す", () => With(w =>
        {
            Keymap.Reset();
            SaveKeys();
        }));
        CommandTable.Register("keyboard:toggle", "画面のキーボードを出す・しまう", () => With(w => osk.Shown = !osk.Shown));
        CommandTable.Register("game:pause", "ゲームを止める・続ける", () => With(w => TogglePause())).OverModal = true;
        CommandTable.Register("game:restart", "ゲームを最初から作り直す", () => With(w => GameApp.Restart())).OverModal = true;
        var back = CommandTable.Register("game:step-back", "1 frame 戻す", () => With(w => StepBack()));
        back.Repeats = true;
        back.OverModal = true;
        var forward = CommandTable.Register("game:step-forward", "1 frame 進める", () => With(w => StepForward()));
        forward.Repeats = true;
        forward.OverModal = true;
        CommandTable.Register("game:compare", "編集中の版を並べる・やめる", () => With(w => ToggleCompare()));
        CommandTable.Register("game:copy-to-draft", "保存済みの版の状態を編集中の版に写す", () => With(w => DraftGame.CopyPending = true));
        CommandTable.Register("search:open", "file と宣言と命令を探す", () => With(w => Search.Open(w)));
        CommandTable.Register("code:go-to-definition", "定義へ飛ぶ", () => With(w => Search.Jump(w)));
        CommandTable.Register("code:back", "飛ぶ前の場所へ戻る", () => With(w => w.Back()));
        CommandTable.Register("code:next-error", "次のエラーの行へ", () => With(w =>
        {
            var error = w.NextError();
            if (error != null)
                Search.GoToPlace(w, error.Path, error.Line);
        }));
        CommandTable.Register("code:diff-side", "動いている版との違いの出し方を替える", () => With(w => PanelView.SideBySide = !PanelView.SideBySide));
        CommandTable.Register("file:delete", "file を消す", () => OnCode(w => w.AskDeleteFocus()));
        CommandTable.Register("window:close", "ウィンドウを閉じる (コードはタブを閉じる)", () => With(w => CloseFocused(w))).Repeats = true;
        CommandTable.Register("tab:next", "隣のタブへ", () => OnCode(w => w.NextTab(1))).Repeats = true;
        CommandTable.Register("tab:previous", "前のタブへ", () => OnCode(w => w.NextTab(-1))).Repeats = true;
        CommandTable.Register("tab:move-left", "タブを左のウィンドウへ移す", () => OnCode(w => w.MoveTab(-1))).Repeats = true;
        CommandTable.Register("tab:move-right", "タブを右のウィンドウへ移す", () => OnCode(w => w.MoveTab(1))).Repeats = true;
        CommandTable.Register("window:focus-left", "左のウィンドウへ", () => With(w => FocusToward(w, -1, 0))).Repeats = true;
        CommandTable.Register("window:focus-right", "右のウィンドウへ", () => With(w => FocusToward(w, 1, 0))).Repeats = true;
        CommandTable.Register("window:focus-up", "上のウィンドウへ", () => With(w => FocusToward(w, 0, 1))).Repeats = true;
        CommandTable.Register("window:focus-down", "下のウィンドウへ", () => With(w => FocusToward(w, 0, -1))).Repeats = true;
        for (var i = 1; i <= 9; i++)
        {
            var n = i - 1;
            CommandTable.Register("window:focus-" + i, i + " 番目のコードのウィンドウへ", () => With(w =>
            {
                w.FocusCode();
                w.FocusNth(n);
            }));
        }
        CommandTable.Register("window:move-back", "ウィンドウを並びの前へ動かす", () => Moved(w => w.MoveFocus(-1))).Repeats = true;
        CommandTable.Register("window:move-forward", "ウィンドウを並びの後ろへ動かす", () => Moved(w => w.MoveFocus(1))).Repeats = true;
        CommandTable.Register("window:grow", "ウィンドウを大きくする", () => Moved(w => w.FocusWindow!.Scale = Math.Min(Grab.MaxScale, w.FocusWindow.Scale * 1.1f))).Repeats = true;
        CommandTable.Register("window:shrink", "ウィンドウを小さくする", () => Moved(w => w.FocusWindow!.Scale = Math.Max(Grab.MinScale, w.FocusWindow.Scale / 1.1f))).Repeats = true;
        CommandTable.Register("window:widen", "ウィンドウの幅を広げる", () => With(w => GrowFocused(w, SizeStep, 0))).Repeats = true;
        CommandTable.Register("window:narrow", "ウィンドウの幅を狭める", () => With(w => GrowFocused(w, -SizeStep, 0))).Repeats = true;
        CommandTable.Register("window:heighten", "ウィンドウの高さを広げる", () => With(w => GrowFocused(w, 0, SizeStep))).Repeats = true;
        CommandTable.Register("window:lower", "ウィンドウの高さを狭める", () => With(w => GrowFocused(w, 0, -SizeStep))).Repeats = true;
        CommandTable.Register("layout:more-mains", "先頭に 1 枚ずつ並べるウィンドウを増やす", () => With(w => TellLayout(w, LayoutMessage.MoreMains))).Repeats = true;
        CommandTable.Register("layout:fewer-mains", "先頭に 1 枚ずつ並べるウィンドウを減らす", () => With(w => TellLayout(w, LayoutMessage.FewerMains))).Repeats = true;
        CommandTable.Register("window:zen", "ウィンドウを画面いっぱいに広げる・戻す", () => With(w => Zen = !Zen));
        CommandTable.Register("window:move-to-next-screen", "ウィンドウを次のスクリーンへ移す", () => With(w =>
        {
            if (w.Focused != null)
                MoveToNextScreen(w, w.Focused);
        }));
        CommandTable.Register("arrange:next", "次の配置へ", () => With(w => NextArrangement()));
        CommandTable.Register("arrange:save", "今の配置を、名前を付けて控える", () => With(w => Search.Ask(w, "配置の名前", SaveArrangement)));
        CommandTable.Register("state:save", "今のゲームの状態を、名前を付けて控える", () => With(w => Search.Ask(w, "状態の名前", SaveState)));
        savedStates.Clear();
        StateNotice = "";
        CommandTable.Register("inspector:toggle", "インスペクタを開く・focus を移す・閉じる", () => With(w =>
        {
            // If not shown, show it and move focus; if focus is elsewhere, move focus; if it has focus, close it
            if (inspect.Shown && w.Focused == inspectWindow)
                Close(w, inspectWindow);
            else
            {
                inspect.Shown = true;
                w.FocusTo(inspectWindow);
            }
        }));
        CommandTable.Register("inspector:load", "インスペクタを値の木と負荷の一覧で切り替える", () => With(w =>
        {
            inspect.Load = !inspect.Shown || !inspect.Load;
            inspect.Shown = true;
        }));
        CommandTable.Register("juice:cycle", "音と演出の強さを変える", () => With(w => SetJuice((Juice + 2) % 3)));
    }

    // Runs a command by name (from search and tests)
    public static bool Run(string name)
    {
        return CommandTable.Run(name);
    }

    // Commands that work with any focus
    static bool With(Action<Workspace> run)
    {
        if (Work == null)
            return false;
        run(Work);
        return true;
    }

    // Commands that work only when focus is on Code
    static bool OnCode(Action<Workspace> run)
    {
        var w = Work;
        if (w == null || w.FocusWindow == null || w.Focused != w.FocusWindow)
            return false;
        run(w);
        return true;
    }

    // Commands that move the focused Code window. While moving, it is lifted the same as when grabbed (while the key repeat continues)
    static bool Moved(Action<Workspace> run)
    {
        if (!OnCode(run))
            return false;
        keyMoved = Work!.FocusWindow;
        keyLift = 0.3f;
        return true;
    }

    // Moves focus to the nearest window on the same screen in the (dx, dy) direction from the focused window (+X is right, +Y is up)
    static void FocusToward(Workspace w, int dx, int dy)
    {
        var from = w.Focused;
        if (from == null || from.Position == null)
        {
            w.FocusBy(dx != 0 ? dx : -dy);
            return;
        }
        var here = Center(w, from);
        Window? best = null;
        var bestCost = 0.0f;
        // Search within the same screen (whether a window on another, overlapping screen is adjacent cannot be decided)
        var on = ScreenFor(w, from);
        foreach (var screen in Screens)
        {
            if (screen != on)
                continue;
            foreach (var window in screen.Windows)
            {
                if (window == from || window.Position == null)
                    continue;
                var at = Center(w, window);
                var along = (at.X - here.X) * dx + (at.Y - here.Y) * dy;
                var across = Math.Abs((at.X - here.X) * dy) + Math.Abs((at.Y - here.Y) * dx);
                // Ones ahead in the direction, whose forward progress is larger than their sideways offset
                if (along <= 0.001f || across > along)
                    continue;
                var cost = along + across * 2;
                if (best == null || cost < bestCost)
                {
                    best = window;
                    bestCost = cost;
                }
            }
        }
        if (best != null)
            w.FocusTo(best);
    }

    // Apparent position of the window center (meters at radius Space.R; straight ahead of the view is 0).
    // Compares windows on flat screens fixed to the view and on cylinder screens placed in the world with the same measure
    static Vec2 Center(Workspace w, Window window)
    {
        var screen = ScreenFor(w, window);
        var at = window.Position!.Value;
        if (OnPlane(screen))
            return new Vec2((screen.Position.X + at.X) * ViewScale(screen), (screen.Position.Y + at.Y) * ViewScale(screen));
        return new Vec2(WindowAngle(w, window) * Space.R - viewX.Value, at.Y);
    }

    static bool Composing()
    {
        return Document.Booted && EditorHost.ImeComposing();
    }

    // While stepping 1 frame from the newest frame, the number of frames left until pausing again
    static int stepping;
    // Whether paused (stepping 1 frame also counts as paused)
    static bool Paused => GameApp.Position >= 0 || stepping > 0;

    // Pauses the game (if paused, resumes from the paused frame)
    public static void TogglePause()
    {
        if (GameApp.Position < 0)
            GameApp.Pause(0);
        else
            GameApp.Resume();
        stepping = 0;
    }

    // While paused, 1 frame back
    public static void StepBack()
    {
        var at = GameApp.Position;
        if (at >= 0 && at + 1 < GameApp.Count)
            GameApp.Pause(at + 1);
    }

    // While paused, 1 frame forward.
    // At the newest frame, runs the game 1 frame, adds it to the record, and pauses there.
    // Resuming recreates the paused frame with the current input, so it runs 2 frames and pauses again on the next frame (pressing while stepping adds 1 frame)
    public static void StepForward()
    {
        var at = GameApp.Position;
        if (stepping > 0)
            stepping++;
        else if (at > 0)
            GameApp.Pause(at - 1);
        else if (at == 0)
        {
            GameApp.Resume();
            stepping = 2;
        }
    }

    // If LIDER_CARET_LOG=1, writes the screen position (px, top left is 0,0) and height of the cursor in the focused panel to standard output when they change,
    // as "lider_caret x y h" (so editing of the recording can follow where typing happens)
    static readonly bool caretLog = Environment.GetEnvironmentVariable("LIDER_CARET_LOG") == "1";
    static string loggedCaret = "";
    // Screen position (px) of the top edge of the cursor in the focused panel, and its height (Z). Unchanged in frames that are not drawn
    public static Vec3 CaretViewport;

    static void LogCaret(Mat4 vp)
    {
        if (!PanelView.CaretDrawn)
            return;
        var top = ViewportOf(vp, PanelView.CaretTop);
        var bottom = ViewportOf(vp, PanelView.CaretBottom);
        CaretViewport = new Vec3(top.X, top.Y, bottom.Y - top.Y);
        var s = "lider_caret " + (int)top.X + " " + (int)top.Y + " " + (int)(bottom.Y - top.Y);
        if (!caretLog || s == loggedCaret)
            return;
        loggedCaret = s;
        Console.WriteLine(s);
    }

    // If LIDER_CARET_LOG=1, writes the file that typed keys go into (the current tab of the focused panel; empty if lider is not taking input,
    // the file search is shown, or there is no panel) to standard output as "lider_focus PATH" when it changes
    // (so the recording tool can check the target file before typing into it)
    static string? loggedFocus;

    static void LogFocus(Workspace w)
    {
        var focus = !w.OnGame && !Search.Shown && w.Focus != null ? w.Focus.Path : "";
        if (!caretLog || focus == loggedFocus)
            return;
        loggedFocus = focus;
        Console.WriteLine("lider_focus " + focus);
    }

    // Screen px of the inspector board in the last drawn frame (left, top, right, bottom)
    public static Vec4 BoardViewport()
    {
        var size = InspectSize();
        var a = ViewportPoint(Space.At(BoardAngle - size.X * 0.5f / Space.R, InspectY() + size.Y * 0.5f, Space.R));
        var b = ViewportPoint(Space.At(BoardAngle + size.X * 0.5f / Space.R, InspectY() - size.Y * 0.5f, Space.R));
        return new Vec4(a.X, a.Y, b.X, b.Y);
    }

    // Screen px of the recorded-frames bar (including the height of the paused-position mark) (left, top, right, bottom). All 0 if not shown
    public static Vec4 TimelineViewport()
    {
        var band = TimelineRect();
        return band != null ? new Vec4(band.X, band.Y, band.X + band.W, band.Y + band.H) : new Vec4(0, 0, 0, 0);
    }

    // Screen px (top left is 0,0), in the last drawn frame, of a position on the code screen (in reference layout coordinates)
    public static Vec2 ViewportPoint(Vec3 p)
    {
        return Work != null ? ViewportOn(Work.CodeScreen, p) : ViewportOf(lastVp, p);
    }

    // Screen px, in the last drawn frame, of a position on screen (in reference layout coordinates)
    public static Vec2 ViewportOn(Screen screen, Vec3 p)
    {
        return ViewportOf(VpOf(screen, lastVp), p);
    }

    // Screen px of a position in space (top left is 0,0)
    static Vec2 ViewportOf(Mat4 vp, Vec3 p)
    {
        var c = vp * new Vec4(p.X, p.Y, p.Z, 1);
        return new Vec2((c.X / c.W * 0.5f + 0.5f) * viewportW, (0.5f - c.Y / c.W * 0.5f) * viewportH);
    }

    // Clicks at screen (x, y) (in VR, the head direction)
    public static void ClickAt(float x, float y)
    {
        var w = Work;
        if (w == null)
            return;
        // Pressing the Space tag returns the game placed in Space to the window on its original screen
        if (OnSpaceTag(lastEye != null ? gaze : PointerRay(x, y)))
        {
            w.TakeFromSpace();
            return;
        }
        pickX = x;
        pickY = y;
        Click(w, lastEye != null ? gaze : PointerRay(x, y), true);
    }

    // Finds the code that drew the game object behind, even if a code board is in front (middle button). Works only while the game is paused
    public static void PickAt(float x, float y)
    {
        var w = Work;
        if (w == null)
            return;
        if (!Paused)
            return;
        pickX = x;
        pickY = y;
        RequestPick(lastEye != null ? gaze : PointerRay(x, y));
    }

    // At the pointed place, does one of the following: a title mark runs its action (close, move to Space); a tab shows that tab; a title moves focus to that window; a panel moves focus to it and moves the cursor; otherwise finds the code that drew the game object (Gfx.Pick)
    public static void Click(Workspace w, Ray ray, bool pickGame)
    {
        if (PressOsk(w, ray))
            return;
        var uv = InspectAt(ray);
        // Focus moves to the pressed window (if a line that jumps to code is pressed, focus moves to the opened Code)
        if (uv != null)
            w.FocusTo(inspectWindow);
        if (uv != null && inspect.Load)
        {
            var tab = inspect.TabAt(uv.Value);
            if (tab >= 0)
                Costs.Metric = tab;
            var load = inspect.LoadAt(uv.Value);
            if (load != null)
            {
                w.FocusCode();
                Search.GoToPlace(w, load.Path, load.Line - 1);
            }
            return;
        }
        if (uv != null)
        {
            var crumb = inspect.CrumbAt(uv.Value);
            var row = inspect.RowAt(uv.Value);
            if (crumb >= 0)
                ScopeTo(crumb);
            else if (row != null && row.Target != "" && InspectView.OnJump(uv.Value))
            {
                Fields.Select(row.Key);
                w.FocusCode();
                Search.GoTo(w, row.Target);
            }
            else if (row != null && row.Scopable && InspectView.OnScope(uv.Value))
                EnterScope(row);
            else if (row != null && row.Openable && !row.Cycle)
                inspect.Tree.Toggle(row.Key);
            else if (row != null && Editable(row))
                EditValue(w, row.Key, row.Text);
            return;
        }
        var edged = !Zen && lastEye == null ? EdgeWindow(w, ray) : null;
        if (edged != null)
        {
            // A Code window with its own size also takes focus when dragged. Dragging a section border does not move focus
            if (edged.Content is Code && ScreenFor(w, edged).Layout is Strip)
                w.FocusTo(edged);
            StartEdge(edged, foundEdge);
            return;
        }
        var head = TitleAt(w, ray);
        if (head != null)
        {
            var title = TitleOf(head.Window);
            var mark = title.MarkAt(head.Uv.X);
            if (mark == TitleView.Close)
                Close(w, head.Window);
            else if (mark == SpaceMark)
                w.PutInSpace(head.Window);
            else if (title.TabAt(head.Uv.X) >= 0)
                ShowTab(head.Window, title.TabAt(head.Uv.X));
            else
                w.FocusTo(head.Window);
            return;
        }
        var hit = WindowAt(w, ray);
        if (hit != null && text != null)
        {
            w.FocusTo(hit.Window);
            var mark = Panel(hit.Window).MarkAt(hit.Uv.X * hit.Window.PxW, hit.Uv.Y * hit.Window.PxH);
            if (mark != null)
            {
                if (mark.Id != "")
                    ToggleInstance(mark.Class, mark.Id);
                else
                    Fields.NextPage(mark.Class);
                return;
            }
            // While paused, pressing the value after a line rewrites that value
            var value = Paused ? Panel(hit.Window).ValueAt(hit.Window.Code.Doc, hit.Uv.X * hit.Window.PxW, hit.Uv.Y * hit.Window.PxH) : null;
            if (value != null)
            {
                EditValue(w, value.Path, value.Text);
                return;
            }
            pressed = hit.Window;
            EditorHost.Press(hit.Window.Code.ViewId, hit.Uv.X * hit.Window.PxW, hit.Uv.Y * hit.Window.PxH);
            return;
        }
        if (pickGame && Paused)
            RequestPick(ray);
    }

    // Shows hints for operations on what the ray points at, in a fixed place at the bottom right (call outside the pass to the screen)
    // If LIDER_HINTS=0, operation hints are not shown
    static readonly bool hintsOff = Environment.GetEnvironmentVariable("LIDER_HINTS") == "0";

    static void ShowHints(Workspace w, Ray ray)
    {
        HoverMarks(w, ray);
        if (text == null)
            return;
        if (hintsOff)
        {
            // Operation hints are not shown, but the load tag is
            HideHints();
            var detail = DetailHints(w, ray);
            if (detail.Count > 0)
            {
                hintCard.Prepare(detail, text);
                PlaceHints(w);
            }
            return;
        }
        var c = new HintContext
        {
            Vr = lastEye != null,
            InspectShown = inspect.Shown,
            Zen = Zen,
            Differs = w.FocusWindow != null && w.FocusWindow.Code.Doc.Differs(),
            SideBySide = PanelView.SideBySide,
            Paused = Paused,
            Errors = w.NextError() != null,
            What = What,
            Juice = Juice,
        };
        var uv = InspectAt(ray);
        var closing = markHover != null && TitleOf(markHover).Marks[markHoverAt] == TitleView.Close;
        var edged = uv == null && lastEye == null && !Zen ? EdgeWindow(w, ray) : null;
        edgeHoverWindow = edged;
        edgeHover = foundEdge;
        if (edged != null)
            c.Target = HintTarget.Edge;
        else if (closing)
            c.Target = HintTarget.Close;
        else if (uv != null)
        {
            c.Target = HintTarget.Inspect;
            var row = inspect.Load ? null : inspect.RowAt(uv.Value);
            c.Openable = row != null && row.Openable && !row.Cycle;
            c.Editable = !inspect.Load && row != null && Editable(row);
            c.Open = row != null && row.Open;
            c.Jump = inspect.Load ? inspect.LoadAt(uv.Value) != null : row != null && row.Target != "" && InspectView.OnJump(uv.Value);
            c.Scope = row != null && row.Scopable && InspectView.OnScope(uv.Value);
            c.Crumb = !inspect.Load && inspect.CrumbAt(uv.Value) >= 0;
        }
        else if (WindowAt(w, ray) is WindowHit over)
        {
            c.Target = HintTarget.Window;
            var mark = Panel(over.Window).MarkAt(over.Uv.X * over.Window.PxW, over.Uv.Y * over.Window.PxH);
            c.InstanceMark = mark != null && mark.Id != "";
            c.Editable = Paused && Panel(over.Window).ValueAt(over.Window.Code.Doc, over.Uv.X * over.Window.PxW, over.Uv.Y * over.Window.PxH) != null;
            c.MoreMark = mark != null && mark.Id == "";
        }
        var hints = DetailHints(w, ray);
        foreach (var h in HintRules.For(c))
            hints.Add(h);
        hintCard.Prepare(hints, text);
        PlaceHints(w);
    }

    // Hints while the mouse belongs to the game: shows only operations that do not depend on what is pointed at
    static void ShowGameHints(Workspace w)
    {
        if (text == null || hintsOff)
            return;
        hintCard.Prepare(HintRules.For(new HintContext
        {
            InspectShown = inspect.Shown,
            Zen = Zen,
            Paused = Paused,
            Errors = w.NextError() != null,
        }), text);
        PlaceHints(w);
    }

    // Size the hint window to fit the tag text and place it by the screen rule (bottom right corner)
    // Put the on-screen keyboard window on a screen only while shown, and size it to the image. It is first put on desk, and afterwards stays where it was moved
    static void PlaceOsk(Workspace w)
    {
        var on = w.ScreenOf(oskWindow);
        if (on != null && !osk.Shown)
        {
            on.Windows.Remove(oskWindow);
            oskWindow.Position = null;
            oskWindow.ViewportRect = null;
            oskWindow.ContentRect = null;
            osk.Point(0, -1, -1);
            osk.Point(1, -1, -1);
            return;
        }
        if (!osk.Shown)
            return;
        if (on == null)
        {
            desk.Windows.Add(oskWindow);
            on = desk;
        }
        var perPx = PanelView.BaseW / PanelView.BasePxW * OskScale;
        oskWindow.Size = new Vec2(OskView.PxW * perPx, OskView.PxH(osk) * perPx);
        if (on.Layout is Stack)
            on.Arrange();
    }

    // Where the ray points on the on-screen keyboard (0..1 in the image; null if not pointing at it)
    static Vec2? OskUv(Workspace w, Ray ray)
    {
        var on = w.ScreenOf(oskWindow);
        if (on == null || !on.Shown || oskWindow.Position == null || oskWindow.Size.X <= 0)
            return null;
        var at = SurfacePoint(on, ray);
        if (at == null)
            return null;
        var u = (at.Value.X - oskWindow.Position.Value.X) / oskWindow.Size.X + 0.5f;
        var v = 0.5f - (at.Value.Y - oskWindow.Position.Value.Y) / oskWindow.Size.Y;
        if (u < 0 || u >= 1 || v < 0 || v >= 1)
            return null;
        return new Vec2(u, v);
    }

    // Highlights the key that the ray of pointer (left hand 0, right hand or mouse 1) points at (no highlight if there is no ray)
    static void PointOsk(Workspace w, int pointer, Ray? ray)
    {
        var uv = ray != null ? OskUv(w, ray.Value) : null;
        osk.Point(pointer, uv != null ? uv.Value.X : -1, uv != null ? uv.Value.Y : -1);
    }

    // If the ray points at a key of the on-screen keyboard, presses that key (sends the same event as a connected keyboard). Returns true if it points at the keyboard
    public static bool PressOsk(Workspace w, Ray ray, int pointer = 1)
    {
        var uv = OskUv(w, ray);
        if (uv == null)
            return false;
        osk.Point(pointer, uv.Value.X, uv.Value.Y);
        var key = osk.Hovered(pointer);
        if (key != null)
            foreach (var e in osk.Press(key))
                OnEvent(e);
        return true;
    }

    static void PlaceHints(Workspace w)
    {
        hintCard.Shown = true;
        hintWindow.Size = new Vec2(hintCard.UsedW() / ViewScale(hud), hintCard.Height() / ViewScale(hud));
        hud.Arrange();
        hintCard.Angle = WindowAngle(w, hintWindow);
        hintCard.Y = WindowY(w, hintWindow);
    }

    // If pointing at a row of the load list or at the load to the right of a panel line, shows that line's load as a text-only line (above the hint tag)
    static List<Hint> DetailHints(Workspace w, Ray ray)
    {
        var hints = new List<Hint>();
        if (!Costs.Enabled)
            return hints;
        LineCost? cost = null;
        var place = "";
        var passes = "";
        var uv = InspectAt(ray);
        if (uv != null)
        {
            var load = inspect.LoadAt(uv.Value);
            if (load != null && load.Cost != null)
            {
                cost = load.Cost;
                place = load.Place + (load.Sub != "" ? " ← " + load.Sub : "") + "  " + load.Where;
                passes = load.Draws > 0 ? load.Draws + " draws" : "";
            }
        }
        else if (WindowAt(w, ray) is WindowHit over)
        {
            var doc = over.Window.Code.Doc;
            var line = Panel(over.Window).CostLineAt(over.Uv.X * over.Window.PxW, over.Uv.Y * over.Window.PxH);
            var live = line < 0 ? -1 : doc.LiveLine(line - 1);
            cost = live < 0 ? null : Costs.At(doc.Path, live + 1);
            passes = live < 0 ? "" : Costs.PassesAt(doc.Path, live + 1);
            var method = Search.MethodAt(doc.Path, line);
            place = (method != "" ? method + "  " : "") + w.Relative(doc.Path) + ":" + line;
        }
        if (cost != null)
            foreach (var s in LoadProfile.Detail(cost, place, passes))
                hints.Add(new Hint(HintIcon.None, "", s));
        return hints;
    }

    // While the mouse and controllers belong to the game, clears the hints and highlight for the pointed thing
    static void HideHints()
    {
        hintCard.Shown = false;
        markHover = null;
        markHoverAt = -1;
        edgeHoverWindow = null;
        pointer = null;
        What = new List<string>();
    }

    static readonly int[] NoGaps = new int[0];

    // Width between the focused panel and the running-version board to its left (meters on the surface; 0 if not shown)
    static float OldBoardW
    {
        get
        {
            var window = Work?.FocusWindow;
            if (window == null || !Panel(window).OldShown)
                return 0;
            var m = MotionOf(window);
            return PanelView.OldW * (m != null ? m.Scale.Value : window.Scale) + PanelView.OldGap;
        }
    }

    // Places the inspector window on a screen if shown, and removes it if hidden
    static void PlaceInspector(Workspace w)
    {
        // Put it on a screen only while shown. It is first put on the screen where the code panels go. Afterwards, it stays on the screen it was moved to
        var on = w.ScreenOf(inspectWindow);
        var home = w.Home ?? w.CodeScreen;
        if (on != null && !inspect.Shown)
        {
            on.Windows.Remove(inspectWindow);
            inspectWindow.Position = null;
            inspectWindow.ViewportRect = null;
            on = null;
        }
        if (inspect.Shown && on == null)
        {
            // Put it leftmost on the code screen (single row), and last on other screens
            if (home == w.CodeScreen)
                home.Windows.Insert(0, inspectWindow);
            else
                home.Windows.Add(inspectWindow);
            on = home;
        }
        // While an area is set, line it up right to the left of the focused panel (fit these 2 into the area)
        if (on == w.CodeScreen && AreaW > 0 && w.FocusWindow != null && w.ScreenOf(w.FocusWindow) == on)
        {
            on.Windows.Remove(inspectWindow);
            on.Windows.Insert(on.Windows.IndexOf(w.FocusWindow), inspectWindow);
        }
        // The first code panel goes in the middle of the code screen (the inspector is lined up to its left).
        // While fixed to the view, the view cannot be moved sideways, so the focused window is put in the middle
        w.CodeScreen.Origin = null;
        if (w.CodeScreen.Anchor == ScreenAnchor.View && w.Focused != null && w.CodeScreen.Windows.Contains(w.Focused))
            w.CodeScreen.Origin = w.Focused;
        foreach (var window in w.CodeScreen.Windows)
            if (window.Content is Code && w.CodeScreen.Origin == null)
                w.CodeScreen.Origin = window;
        // In a single row, the inspector's pixel count and scale decide its size
        inspectWindow.PxW = InspectView.PxW;
        inspectWindow.PxH = InspectView.PxH;
        inspectWindow.Scale = InspectScale;
        // On flat screens, fit it into the rectangle decided by the arrangement rule
        if (on != null && OnPlane(on) && inspectWindow.Position != null)
            InspectView.SetSize(inspectWindow.Size.X * ViewScale(on) / InspectScale, (inspectWindow.Size.Y * ViewScale(on) - InspectTitleH) / InspectScale);
        // Do not put focus on a hidden window
        if (w.Focused == inspectWindow && !TreeVisible)
            w.FocusCode();
    }

    // Inspector's center angle (used by tests that press on the inspector), height, and size (meters at radius Space.R)
    public static float BoardAngle => Work != null ? WindowAngle(Work, inspectWindow) : 0;

    static float InspectY()
    {
        return Work != null ? WindowY(Work, inspectWindow) : 0;
    }

    static Vec2 InspectSize()
    {
        var screen = Work != null ? ScreenFor(Work, inspectWindow) : null;
        // On flat screens, the title also fits in the window rectangle
        return screen != null && OnPlane(screen)
            ? new Vec2(inspectWindow.Size.X * ViewScale(screen), inspectWindow.Size.Y * ViewScale(screen) - InspectTitleH)
            : inspectWindow.Size;
    }

    // Height of the inspector's title band, and the center height
    static float InspectTitleH
    {
        get
        {
            var screen = Work != null ? ScreenFor(Work, inspectWindow) : null;
            return screen != null && OnPlane(screen) ? BarH(screen, inspectWindow) : PanelView.TitleH * InspectScale;
        }
    }

    // On flat screens, height of the title band taken at the top edge of window's rectangle (meters at radius Space.R)
    static float BarH(Screen screen, Window window)
    {
        var whole = window.ViewportRect;
        return whole != null && whole.W > 0 ? window.TitleBar * window.Size.X / whole.W * ViewScale(screen) : 0;
    }

    static float InspectTitleY()
    {
        return InspectY() + (InspectSize().Y + InspectTitleH) * 0.5f;
    }

    // If pointing at the inspector, the position within it
    static Vec2? InspectAt(Ray ray)
    {
        var w = Work;
        if (w == null || !TreeVisible)
            return null;
        var screen = ScreenFor(w, inspectWindow);
        // Images on flat screens are drawn before Code, so parts hidden behind Code cannot be pointed at
        if (OnPlane(screen) && WindowAt(w, ray) != null)
            return null;
        var size = InspectSize();
        return HitOn(screen, ray, BoardAngle, InspectY(), size.X, size.Y);
    }

    // If pointing over the pressed panel, select from the pressed point to there
    static void DragTo(Workspace w, Ray ray)
    {
        if (pressed == null || !w.Windows.Contains(pressed))
            return;
        var uv = HitWindow(w, pressed, ray);
        if (uv != null)
            EditorHost.Drag(pressed.Code.ViewId, uv.Value.X * pressed.PxW, uv.Value.Y * pressed.PxH);
    }

    static void ReleasePress(Workspace w)
    {
        if (pressed == null)
            return;
        EditorHost.Release(pressed.Code.ViewId, 0, 0);
        pressed = null;
    }

    static void RequestPick(Ray ray)
    {
        pickPending = true;
        hoverPick = false;
        // Inspect only when pointing at the game image of the version lider is looking at (in XR the game is not drawn in space, so it cannot be pointed at)
        if (lastEye != null)
            return;
        PointGames(pickX, pickY);
        if (gamePointed != null && gamePointed == GameWindow(Watched.Version))
            Watched.Pick(gameUv);
    }

    // Panel body text glyphs at the density that fits the current screen
    static MonoText PanelText()
    {
        var density = 1;
        if (Space.Flat)
        {
            Gfx.Size(out _, out var sh);
            // On web, Gfx.Size is in CSS px, so the page passes the pixel ratio
            density = PanelView.DensityFor((int)(sh * PixelRatio));
        }
        if (density == 1)
            return text!;
        if (!panelTexts.TryGetValue(density, out var t))
        {
            t = new MonoText("lider_mono_" + density, Ttf(), 15, density);
            panelTexts[density] = t;
        }
        return t;
    }

    static string Ttf()
    {
        return (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/UDEVGothic-Regular.ttf";
    }

    // Board for each panel view
    public static PanelView Panel(Window window)
    {
        var key = window.Code.ViewId.ToString();
        if (!panels.TryGetValue(key, out var panel))
        {
            panel = new PanelView("lider_panel_" + panels.Count, window.Code.ViewId);
            panels[key] = panel;
        }
        return panel;
    }

    static Ray PointerRay(float x, float y)
    {
        return Camera3d.ViewportRay(lastVp, x, y, viewportW, viewportH);
    }

    // Screen that window is placed on
    static Screen ScreenFor(Workspace w, Window window)
    {
        return w.ScreenOf(window) ?? w.CodeScreen;
    }

    // Whether it is a flat screen (regardless of what it is fixed to).
    // Things on it are drawn and hit at radius Space.R so they have the same apparent size from straight in front of the viewer, then carried to the screen's distance by Pose
    static bool OnPlane(Screen screen)
    {
        return screen.Shape == ScreenShape.Plane;
    }

    // The view-projection in view coordinates (non-XR: the camera with the default field of view; XR: the eye being drawn), and the matrix from view coordinates to lider's space (identity in non-XR)
    static Mat4 viewVp = Mat4.Identity();
    static Mat4 flatViewVp = Mat4.Identity();
    static Mat4 headToWorld = Mat4.Identity();

    // Whether screen is drawn in viewer coordinates (rather than world coordinates).
    // Always for screens fixed to the view. Outside XR, flat screens too: sliding and zooming the view is for reading the row of panels on the cylinder screen, and does not move flat screens
    static bool ByViewer(Screen screen)
    {
        return screen.Anchor == ScreenAnchor.View || (lastEye == null && OnPlane(screen));
    }

    // Turns a ray in lider's space into the ray used to hit panels on screen (a ray in view coordinates for screens fixed to the view)
    static Ray RayOn(Screen screen, Ray ray)
    {
        if (ByViewer(screen))
        {
            if (lastEye != null)
            {
                var toHead = headToWorld.Inverse();
                ray = new Ray(toHead.MulPoint(ray.Origin), toHead.MulDir(ray.Dir));
            }
            else
            {
                // Ray in view coordinates through the same display pixel
                var px = ViewportOf(lastVp, ray.At(1));
                ray = Camera3d.ViewportRay(flatViewVp, px.X, px.Y, viewportW, viewportH);
            }
        }
        var back = Pose(screen).Inverse();
        return new Ray(back.MulPoint(ray.Origin), back.MulDir(ray.Dir));
    }

    // Matrix that carries screen from the reference layout (cylinder axis through the anchor's origin, height 0) to its current position.
    // Things on the screen are drawn and hit in reference layout coordinates, and carried by this matrix.
    // Flat screens put their position into the surface coordinates and are drawn at radius Space.R, so they are scaled from the origin by the distance ratio and carried to the screen's distance
    static Mat4 Pose(Screen screen)
    {
        if (OnPlane(screen))
        {
            var far = screen.Position.Z / Space.R;
            return Mat4.Scale(new Vec3(far, far, far));
        }
        var deeper = screen.Radius - Space.R;
        // While the cylinder is unrolled flat, carry straight back
        if (!Xr.Active())
            return Mat4.Translate(new Vec3(0, screen.Position.Y, deeper));
        return Mat4.Translate(new Vec3(deeper * (float)Math.Sin(screen.Angle), screen.Position.Y, deeper * (float)Math.Cos(screen.Angle)));
    }

    // View-projection for drawing things on screen (world is the view-projection in world coordinates)
    static Mat4 VpOf(Screen screen, Mat4 world)
    {
        return (ByViewer(screen) ? viewVp : world) * Pose(screen);
    }

    // Where ray points on the board of width w and height h centered at angle angle and height y on screen (0..1 within the board).
    // Flat screens are not curved, even in XR
    static Vec2? HitOn(Screen screen, Ray ray, float angle, float y, float w, float h)
    {
        var flat = Space.Flat;
        Space.Flat = flat || screen.Shape == ScreenShape.Plane;
        var uv = Space.HitCurved(RayOn(screen, ray), angle, y, Space.R, w, h);
        Space.Flat = flat;
        return uv;
    }

    // Factor that turns a length on a flat screen (distance Position.Z) into the same apparent length at radius Space.R
    static float ViewScale(Screen screen)
    {
        return Space.R / screen.Position.Z;
    }

    // Cylinder angle of a code panel (lays the panels out if not laid out yet)
    public static float WindowAngle(Workspace w, Window window)
    {
        if (window.Position == null)
            w.Arrange(grab.Window);
        if (window.Position == null)
            return 0;
        var screen = ScreenFor(w, window);
        return OnPlane(screen) ? (screen.Position.X + window.Position.Value.X) / screen.Position.Z : screen.AngleAt(window.Position.Value.X);
    }

    // Child board of a code window that shows the running version side by side (null if there is none)
    public static Window? DiffOf(Window window)
    {
        foreach (var child in window.Children)
            if (child.Content is Diff)
                return child;
        return null;
    }

    // Make the running-version board a child of the code window only while shown, and place it in front, to the left of the parent (the parent's order does not change)
    static void PlaceDiff(Window window, bool shown)
    {
        var board = DiffOf(window);
        if (!shown)
        {
            if (board != null)
                window.Disown(board);
            return;
        }
        if (board == null)
        {
            board = new Window(new Diff(window.Code));
            window.Adopt(board);
        }
        board.Size = new Vec2(PanelView.OldW * window.Scale, window.UnscaledSize.Y * window.Scale);
        board.Position = new Vec3(-(window.UnscaledSize.X * window.Scale * 0.5f + PanelView.OldGap + board.Size.X * 0.5f), 0, InspectR - Space.R);
    }

    // Whether it is a window that, on flat screens, fits a title band drawn separately from the body at the top edge of the window rectangle
    static bool TitleInRect(Window window)
    {
        return window.Content is Code || window.Content is Inspector;
    }

    // Height of the middle of a code panel's body (0 for panels in a single row; changes only while grabbed)
    public static float WindowY(Workspace w, Window window)
    {
        if (window.Position == null)
            w.Arrange(grab.Window);
        if (window.Position == null)
            return 0;
        var screen = ScreenFor(w, window);
        // On flat screens the title also fits in the window rectangle, so the body moves down by the title height
        return OnPlane(screen)
            ? (screen.Position.Y + window.Position.Value.Y) * ViewScale(screen) - (TitleInRect(window) ? ((window.Content is Inspector) ? BarH(screen, window) : PanelView.TitleH * window.Scale) * 0.5f : 0)
            : window.Position.Value.Y;
    }

    // Panel the ray hits (the focused panel is checked first)
    static WindowHit? WindowAt(Workspace w, Ray ray)
    {
        if (w.FocusWindow != null)
        {
            var uv = HitWindow(w, w.FocusWindow, ray);
            if (uv != null)
                return new WindowHit(w.FocusWindow, uv.Value);
        }
        foreach (var window in w.Windows)
        {
            if (window == w.FocusWindow)
                continue;
            var uv = HitWindow(w, window, ray);
            if (uv != null)
                return new WindowHit(window, uv.Value);
        }
        return null;
    }

    // Where ray hits window (0..1 within the panel)
    static Vec2? HitWindow(Workspace w, Window window, Ray ray)
    {
        // While moving, points at where it is visible
        var m = MotionOf(window);
        var scale = m != null ? m.Scale.Value : window.Scale;
        return HitOn(ScreenFor(w, window), ray, m != null ? m.Angle.Value : WindowAngle(w, window), m != null ? m.Y.Value : WindowY(w, window), window.UnscaledSize.X * scale, window.UnscaledSize.Y * scale);
    }

    // Window title band. The same one is attached to windows of any content, and placement, hit testing, and drawing are handled in one path
    static readonly Dictionary<Window, TitleView> titles = new Dictionary<Window, TitleView>();
    static int titleCount;
    // Title mark being pointed at (window and mark number)
    static Window? markHover;
    static int markHoverAt = -1;
    // Mark to move to Space
    public const string SpaceMark = "↑";

    // window's title band. Its contents (tabs, name, marks, width) are decided from the current state of the window
    public static TitleView TitleOf(Window window)
    {
        if (!titles.TryGetValue(window, out var title))
        {
            title = new TitleView("lider_title_" + titleCount++);
            titles[window] = title;
        }
        var w = Work;
        // A panel in its closing animation does not change its contents
        if (w == null || (window.Content is Code && !w.Windows.Contains(window)))
            return title;
        title.Marks.Clear();
        if (window.Content is Code)
        {
            var motion = MotionOf(window);
            title.Tabs = w.TabTitles(window);
            title.Tab = window.Code.Tab;
            // Show the seconds it took to apply the change after the tabs
            title.Label = motion != null && motion.LandLabel > 0 ? (int)(motion.LandSeconds * 100) / 100.0f + "s" : "";
            title.PxW = Panel(window).PxW;
            title.Marks.Add(TitleView.Close);
        }
        else if (window == inspectWindow)
        {
            // While versions are side by side, also show the name of the version being viewed
            title.Label = (inspect.Load ? "Load" : "Inspector") + (DraftGame.Shown ? ": " + (Watched.Version == GameVersion.Draft ? DraftGame.Label() : "保存済み") : "");
            title.PxW = BarPxW(window) ?? InspectView.PxW;
            title.Marks.Add(TitleView.Close);
        }
        else
        {
            title.Label = GameTitle(window);
            title.PxW = BarPxW(window) ?? Math.Max(1, (int)(window.Size.X * PanelView.BasePxW / PanelView.BaseW));
            if (Closable(window))
                title.Marks.Add(TitleView.Close);
            title.Marks.Add(SpaceMark);
        }
        title.Focused = window == w.Focused;
        title.Lit = window == markHover ? markHoverAt : -1;
        return title;
    }

    // For a window with a band height taken at the top edge of its rectangle, the width of the band image (px, at the scale where the band height is PanelView.TitlePx; null if none is taken)
    static int? BarPxW(Window window)
    {
        if (window.ViewportRect == null || window.TitleBar <= 0)
            return null;
        return Math.Max(1, (int)(window.ViewportRect.W * PanelView.TitlePx / window.TitleBar));
    }

    // Title band placement: center angle and height, width, height on the screen (meters at radius Space.R), and the radius to draw at
    class TitlePlace
    {
        public readonly Screen Screen;
        public readonly float Angle;
        public readonly float Y;
        public readonly float W;
        public readonly float H;
        public readonly float R;

        public TitlePlace(Screen screen, float angle, float y, float w, float h, float r)
        {
            Screen = screen;
            Angle = angle;
            Y = y;
            W = w;
            H = h;
            R = r;
        }
    }

    // Placement of window's title band (null if the band is not shown).
    // The band sits at the top edge of the body. On flat screens it fits in the top edge of the window rectangle
    static TitlePlace? TitlePlaceOf(Workspace w, Window window)
    {
        var screen = ScreenFor(w, window);
        if (window.Content is Code)
        {
            // While moving, attach it where it is visible
            var m = MotionOf(window);
            if (m == null && window.Position == null)
                return null;
            var scale = m != null ? m.Scale.Value : window.Scale;
            return new TitlePlace(screen, m != null ? m.Angle.Value : WindowAngle(w, window), Panel(window).TitleY(m != null ? m.Y.Value : WindowY(w, window), scale),
                window.UnscaledSize.X * scale, PanelView.TitleH * scale, Space.R + (m != null ? m.Z.Value : 0));
        }
        if (window == inspectWindow)
            return TreeVisible ? new TitlePlace(screen, BoardAngle, InspectTitleY(), InspectSize().X, InspectTitleH, Space.R) : null;
        if (!(window.Content is GameView) || window.Position == null || window == w.InSpace || !screen.Shown || !LiderShown(w))
            return null;
        var at = window.Position.Value;
        if (!OnPlane(screen))
            return new TitlePlace(screen, WindowAngle(w, window), WindowY(w, window) + (window.Size.Y + PanelView.TitleH * window.Scale) * 0.5f,
                window.Size.X, PanelView.TitleH * window.Scale, Space.R);
        var whole = window.ViewportRect;
        if (whole == null || whole.W <= 0 || window.TitleBar <= 0)
            return null;
        // Convert the band height on the display into a length on the screen surface
        var k = ViewScale(screen);
        var bar = window.TitleBar * window.Size.X / whole.W;
        return new TitlePlace(screen, (screen.Position.X + at.X) / screen.Position.Z, (screen.Position.Y + at.Y + (window.Size.Y - bar) * 0.5f) * k,
            window.Size.X * k, bar * k, Space.R);
    }

    // Windows with a title band. Order of drawing in front: images on non-flat screens (inspector, game image), Code (focused window first), images on flat screens
    static List<Window> Titled(Workspace w)
    {
        var all = new List<Window>();
        foreach (var plane in new List<bool> { false, true })
        {
            foreach (var screen in Screens)
                foreach (var window in screen.Windows)
                    if ((window.Content is GameView || window.Content is Inspector) && OnPlane(screen) == plane)
                        all.Add(window);
            if (plane)
                break;
            if (w.FocusWindow != null)
                all.Add(w.FocusWindow);
            foreach (var window in w.Windows)
                if (window != w.FocusWindow)
                    all.Add(window);
        }
        return all;
    }

    // Title band the ray hits (windows drawn in front are checked first).
    // Images on flat screens are drawn before Code, so bands hidden behind Code cannot be pointed at
    static WindowHit? TitleAt(Workspace w, Ray ray)
    {
        foreach (var window in Titled(w))
        {
            var uv = HitTitle(w, window, ray);
            if (uv == null)
                continue;
            if (!(window.Content is Code) && OnPlane(ScreenFor(w, window)) && WindowAt(w, ray) != null)
                return null;
            return new WindowHit(window, uv.Value);
        }
        return null;
    }

    static Vec2? HitTitle(Workspace w, Window window, Ray ray)
    {
        var place = TitlePlaceOf(w, window);
        return place != null ? HitOn(place.Screen, ray, place.Angle, place.Y, place.W, place.H) : null;
    }

    // Remembers the title mark the ray points at (its background is highlighted)
    static void HoverMarks(Workspace w, Ray ray)
    {
        markHover = null;
        markHoverAt = -1;
        var head = TitleAt(w, ray);
        var at = head != null ? TitleOf(head.Window).MarkIndexAt(head.Uv.X) : -1;
        if (head == null || at < 0)
            return;
        markHover = head.Window;
        markHoverAt = at;
    }

    // Redraws the title band textures (call outside the pass to the screen)
    static void PrepareTitles(Workspace w, MonoText text)
    {
        var live = Titled(w);
        // Panels in their closing animation keep being drawn too
        foreach (var pair in motions)
            if (!live.Contains(pair.Key))
                live.Add(pair.Key);
        var gone = new List<Window>();
        foreach (var pair in titles)
            if (!live.Contains(pair.Key))
                gone.Add(pair.Key);
        foreach (var window in gone)
            titles.Remove(window);
        foreach (var window in live)
            if (TitlePlaceOf(w, window) != null)
                TitleOf(window).Prepare(text);
    }

    // Draws window's title band (world is the view-projection in world coordinates)
    static void DrawTitle(Workspace w, Window window, Mat4 world)
    {
        var place = TitlePlaceOf(w, window);
        if (place == null || !titles.TryGetValue(window, out var title) || title.Texture == null)
            return;
        var flat = Space.Flat;
        Space.Flat = flat || OnPlane(place.Screen);
        var on = VpOf(place.Screen, world);
        var k = place.R / Space.R;
        // For Code, the board (PanelView) also draws the title background
        if (!(window.Content is Code))
            Theme.Plate(on, Theme.Surface, place.Angle, place.Y, Space.R, place.W, place.H);
        Quad3d.Curved(on, title.Texture, Color.Rgb(1, 1, 1, 1), place.Angle, place.Y * k, place.R, place.W * k, place.H * k, 0, 1);
        Space.Flat = flat;
    }

    // When the Gfx.Pick result arrives: for a click, opens the first file in the caller chain that is in the list, at that line; when only pointing, puts it in What
    static void TakePick(Workspace w)
    {
        if (!pickPending)
            return;
        var hit = Watched.PickResult();
        if (hit == null)
            return;
        pickPending = false;
        if (hoverPick)
        {
            What = hit.Draw < 0 ? new List<string>() : WhatOf(w, hit.Site);
            return;
        }
        if (hit.Draw < 0)
            return;
        var sites = hit.Site.Split('\n');
        var selves = hit.Self.Split('\n');
        for (var i = 0; i < sites.Length; i++)
        {
            var site = sites[i];
            var colon = TraceLog.LastColon(site);
            if (colon <= 0 || !int.TryParse(Utf8.Sub(site, colon + 1), out var line))
                continue;
            var path = w.Resolve(Utf8.Sub(site, 0, colon));
            if (path == null)
                continue;
            w.Remember(w.Here());
            w.FocusCode();
            w.FocusOn(path);
            w.Focus?.MoveTo(line - 1, 0);
            // Narrow to the instance that drew it (the this of the stack level of the opened line)
            if (i < selves.Length && selves[i] != "")
                ScopeToInstance(selves[i]);
            return;
        }
    }

    // Turns the callers that are in the list into "method (file:line)" lines (MaxWhat of them, innermost first)
    static List<string> WhatOf(Workspace w, string sites)
    {
        var rows = new List<string>();
        foreach (var site in sites.Split('\n'))
        {
            var colon = TraceLog.LastColon(site);
            if (colon <= 0 || !int.TryParse(Utf8.Sub(site, colon + 1), out var line))
                continue;
            var path = w.Resolve(Utf8.Sub(site, 0, colon));
            if (path == null)
                continue;
            var method = Search.MethodAt(path, line);
            var place = w.Relative(path) + ":" + line;
            rows.Add((rows.Count > 0 ? "← " : "") + (method != "" ? method + " (" + place + ")" : place));
            if (rows.Count == MaxWhat)
                break;
        }
        return rows;
    }

    const int MaxWhat = 3;

    // Inspects what is drawn at screen (x, y) (when pointing without clicking)
    public static void HoverAt(float x, float y)
    {
        pickX = x;
        pickY = y;
        RequestPick(PointerRay(x, y));
        hoverPick = true;
    }

    // While the mouse rests pointing at a game object, inspects what is drawn every HoverSeconds. Cleared when pointing elsewhere or moving
    static void Hover(Workspace w, Ray ray, float mx, float my, float dt)
    {
        var moved = mx != hoverX || my != hoverY;
        hoverX = mx;
        hoverY = my;
        var game = Paused && InspectAt(ray) == null && TitleAt(w, ray) == null && WindowAt(w, ray) == null && !Search.Shown;
        if (moved || !game)
        {
            hoverStill = 0;
            What = new List<string>();
            return;
        }
        hoverStill += dt;
        if (hoverStill < HoverSeconds || pickPending)
            return;
        hoverStill = 0;
        HoverAt(mx, my);
    }

    const float HoverSeconds = 0.25f;

    // Outside XR, sets the screen size and the camera that draws things fixed to the view.
    // If lider's elements have not been drawn yet (while only the game image is shown since startup), the camera that views the world is the default one, with the view not slid
    static void SetCameras(int sw, int sh)
    {
        viewportW = sw;
        viewportH = sh;
        flatViewVp = Camera3d.Vp(new Camera3dOpts
        {
            Eye = new Vec3(0, 0, 0),
            Target = new Vec3(0, 0, 1),
            Fov = Space.FlatFov,
            Near = 0.05f,
            Far = 100,
            Aspect = (float)sw / sh,
        });
        viewVp = flatViewVp;
        headToWorld = Mat4.Identity();
        if (!cameraSet)
            lastVp = Camera3d.Vp(new Camera3dOpts
            {
                Eye = new Vec3(0, 0, 0),
                Target = new Vec3(0, 0, 1),
                Fov = fov,
                Near = 0.05f,
                Far = 100,
                Aspect = (float)sw / sh,
            });
    }

    public static void OnFrame(float dt)
    {
        Gfx.Size(out var sw, out var sh);
        var xr = Xr.Active();
        if (!xr)
            SetCameras(sw, sh);
        // Screens are placed with the default field of view, which does not follow the zoom (fov)
        PutAway();
        // The game lider looks at is the version of the game image window that last had focus (the saved version once the draft version is closed)
        if (Work != null && Work.Focused != null && Work.Focused.Content is GameView seen)
            Watched.Watch(seen.Version);
        if (!DraftGame.Shown)
            Watched.Watch(GameVersion.Saved);
        backdrop.Fill(Space.FlatFov, (float)sw / sh);
        hud.Fill(Space.FlatFov, (float)sw / sh);
        palette.Fill(Space.FlatFov, (float)sw / sh);
        foreach (var screen in Screens)
            if (OnPlane(screen))
            {
                // The display position where the middle of the screen surface appears, and the length that 1 meter on the surface covers on the display.
                // XR has no display, so use the values seen with the default field of view (used for layout inside windows)
                var perM = sh / (2 * screen.Position.Z * (float)Math.Tan(Space.FlatFov * 0.5f * Math.PI / 180));
                var origin = new Vec2(sw * 0.5f + screen.Position.X * perM, sh * 0.5f - screen.Position.Y * perM);
                if (!xr)
                {
                    origin = SurfacePx(screen, 0, 0);
                    perM = SurfacePx(screen, 1, 0).X - origin.X;
                }
                SetTitleBars(screen, perM);
                screen.Project(origin, perM);
            }
        // Outside XR, a game placed in Space is drawn as a full-screen image (in XR, the game draws directly to the eyes)
        var spaced = Work?.InSpace;
        if (spaced != null)
        {
            spaced.TitleBar = 0;
            spaced.ViewportRect = Xr.Active() ? null : new PixelRect(0, 0, sw, sh);
            spaced.ContentRect = spaced.ViewportRect;
        }
        // Pass the camera looking at Space (from the previous frame) in the same form as Xr.View (forward is -Z)
        var eye = lastVp * Mat4.Scale(new Vec3(1, 1, -1));
        GameApp.SpaceVp = new float[]
        {
            eye.M00, eye.M01, eye.M02, eye.M03, eye.M10, eye.M11, eye.M12, eye.M13,
            eye.M20, eye.M21, eye.M22, eye.M23, eye.M30, eye.M31, eye.M32, eye.M33,
        };
        GameApp.InSpace = spaced != null && spaced.Content is GameView inSpace && inSpace.Version == GameVersion.Saved;
        DraftGame.InSpace = spaced != null && !GameApp.InSpace;
        var saved = GameWindow(GameVersion.Saved);
        var draft = GameWindow(GameVersion.Draft);
        // Input goes to both games, as the position within the image of the game window the mouse is over
        Input.MousePos(out var mx, out var my);
        PointGames(mx, my);
        // The keyboard is shown to the game only when focus is on the game, and the mouse only while it points at the game
        var keyboard = Work == null || Work.OnGame;
        var mouse = Work == null || MouseForGame(Work, mx, my);
        if (saved != null)
            GameApp.Step(dt, keyboard, mouse, GamePxW(saved), GamePxH(saved));
        if (draft != null && Work != null)
            DraftGame.Step(dt, keyboard, mouse, GameApp.Position >= 0, GamePxW(draft), GamePxH(draft), Work.Docs);
        // XR has no display (it can only draw to the left and right eyes), so images pasted on the display are drawn as surfaces in space inside the pass to the eyes (DrawScene)
        var flat = !xr;
        if (flat)
            ScreenView.Draw(Screens, sw, sh, spaced?.Content.Texture());
        StepTimeline(dt);
        if (flat)
            DrawTimelineBar(sw, sh);
        var change = GameApp.Changed();
        if (change.Reloaded)
            GameReloaded();
        if (change.Notes != "")
            GameReloadNotes(change.Notes);
        if (change.BuildError != "")
            GameBuildFailed(change.BuildError);
        if (stepping > 0 && --stepping == 0)
            GameApp.Pause(0);
        Frame(dt);
    }

    // Window showing (on the display) the game image of version
    static Window? GameWindow(GameVersion version)
    {
        if (Work != null && Work.InSpace != null && Work.InSpace.Content is GameView spaced && spaced.Version == version)
            return Work.InSpace;
        foreach (var screen in Screens)
            foreach (var window in screen.Windows)
                if (window.Content is GameView view && view.Version == version && window.Position != null)
                    return window;
        return null;
    }

    // Pixel count for drawing the game image. On a flat screen pasted on the display, the pixels of the rectangle on the display; on other screens, pixels that fit the window size
    static int GamePxW(Window window)
    {
        return (int)((window.ContentRect != null ? window.ContentRect.W : window.Size.X * PanelView.BasePxW / PanelView.BaseW) * PixelRatio);
    }

    static int GamePxH(Window window)
    {
        return (int)((window.ContentRect != null ? window.ContentRect.H : window.Size.Y * PanelView.BasePxW / PanelView.BaseW) * PixelRatio);
    }

    // The game image window the mouse last pointed at (null if none), the position within the image, and the width of the image on the display
    static Window? gamePointed;
    static Vec2 gameUv = new Vec2(0.5f, 0.5f);
    static float gameScreenW = 1;

    // Passes the position within the game image that display (x, y) points at to both games.
    // If no image is pointed at, uses a position outside the image of the saved version pasted on the display (for an image placed in space, keeps the last pointed position)
    static void PointGames(float x, float y)
    {
        gamePointed = null;
        foreach (var version in new List<GameVersion> { GameVersion.Draft, GameVersion.Saved })
        {
            var window = GameWindow(version);
            var uv = window != null ? GameUv(window, x, y) : null;
            if (window == null || uv == null)
                continue;
            gamePointed = window;
            gameUv = uv.Value;
            break;
        }
        var saved = GameWindow(GameVersion.Saved);
        if (gamePointed == null && saved != null && saved.ContentRect != null)
        {
            var at = saved.ContentRect;
            gameUv = new Vec2((x - at.X) / at.W, (y - at.Y) / at.H);
            gameScreenW = at.W;
        }
        GameApp.Map.Point(gameUv, gameScreenW);
        DraftGame.Map.Point(gameUv, gameScreenW);
    }

    // If display (x, y) points at window's game image, the position within the image (top left is 0,0). Puts the on-display width of the pointed image in gameScreenW
    static Vec2? GameUv(Window window, float x, float y)
    {
        var at = window.ContentRect;
        if (at != null)
        {
            if (!at.Contains(x, y))
                return null;
            gameScreenW = at.W;
            return new Vec2((x - at.X) / at.W, (y - at.Y) / at.H);
        }
        var w = Work;
        if (w == null || lastEye != null || !ScreenFor(w, window).Shown)
            return null;
        var angle = WindowAngle(w, window);
        var cy = WindowY(w, window);
        var uv = HitOn(ScreenFor(w, window), PointerRay(x, y), angle, cy, window.Size.X, window.Size.Y);
        if (uv == null)
            return null;
        var half = window.Size.X * 0.5f / Space.R;
        gameScreenW = Math.Max(1, Math.Abs(ViewportPoint(Space.At(angle + half, cy, Space.R)).X - ViewportPoint(Space.At(angle - half, cy, Space.R)).X));
        return uv;
    }

    // Text drawn over the images pasted on the display (the recorded-frames bar)
    static MonoText? labelText;
    static SpriteBatch? labelBatch;

    // Windows on flat screens that show an image (game image, inspector) get a title band height.
    // The height has the same apparent size as the Code title band (perM is the length that 1 meter on the surface covers on the display).
    // While lider's elements are hidden (only the game is shown), the band is not shown
    static void SetTitleBars(Screen screen, float perM)
    {
        var bar = (float)Math.Round(PanelView.TitleH * (Space.Flat ? Space.FlatScale : 1) / ViewScale(screen) * perM);
        foreach (var window in screen.Windows)
            if (window.Content is GameView || window.Content is Inspector)
                window.TitleBar = Work != null && LiderShown(Work) ? bar : 0;
    }

    // Whether it is a game image window that can be closed (the saved version cannot be closed)
    static bool Closable(Window window)
    {
        return window.Content is GameView view && view.Version == GameVersion.Draft;
    }

    static string GameTitle(Window window)
    {
        if (Closable(window))
            return DraftGame.Label();
        return DraftGame.Shown ? "保存済み" : "Game";
    }

    static void DrawTimelineBar(int sw, int sh)
    {
        if (TimelineRect() == null)
            return;
        var density = Math.Max(1, (int)Math.Round(PixelRatio));
        if (labelText == null || labelText.Density != density)
            labelText = new MonoText("lider_label_" + density, Ttf(), 15, density);
        if (labelBatch == null || labelBatch.LogicalW != sw || labelBatch.LogicalH != sh)
            labelBatch = new SpriteBatch("lider_label", sw, sh);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        labelBatch.Begin();
        DrawTimeline();
        labelBatch.Flush();
        Gfx.EndPass();
    }

    // Passes event to the game's OnEvent (while side by side, to the draft version too)
    static void SendGame(EventData e)
    {
        if (e.Kind == EventKind.MouseButtonDown || e.Kind == EventKind.MouseButtonUp || e.Kind == EventKind.MouseMotion)
            PointGames(e.X, e.Y);
        GameApp.Send(e);
        DraftGame.Send(e);
    }

    // Shows / stops showing the saved version and the draft version side by side (splits the backdrop screen evenly into left and right)
    public static void ToggleCompare()
    {
        if (DraftGame.Shown)
        {
            DraftGame.Close();
            // If the window being closed has focus, move it to the saved version's window
            if (Work != null && draftWindow != null && Work.Focused == draftWindow)
                Work.FocusTo(gameWindow);
            if (Work != null && draftWindow != null && Work.InSpace == draftWindow)
                Work.TakeFromSpace();
            if (draftWindow != null)
                foreach (var screen in Screens)
                    screen.Windows.Remove(draftWindow);
            draftWindow = null;
            return;
        }
        DraftGame.Open(GameApp.Project);
        draftWindow = new Window(new GameView(GameVersion.Draft));
        // The game image is placed before the code panels
        backdrop.Windows.Insert(1, draftWindow);
    }

    // Whether the code panels are on the same screen as the game image (backdrop).
    // If false, the code panels are on the code screen in front, overlaid translucently on the game image
    public static bool Tiled => Work != null && Work.Home == backdrop;

    // Arrangements: the 2 built-in ones (overlay, side by side) and the ones users recorded. Looked up by name
    public static readonly List<Arrangement> Arrangements = new List<Arrangement>();
    // Arrangements recorded by users (written to a file)
    static readonly List<Arrangement> savedArrangements = new List<Arrangement>();
    // Name of the arrangement restored last
    public static string ArrangementName = "";
    // Whether the file of recorded arrangements has been read
    static bool arrangementsLoaded;
    public const string Overlay = "overlay";
    public const string SideBySide = "side-by-side";

    static string ArrangementsPath(Workspace w)
    {
        return w.Root + "/.lider/arrangements.txt";
    }

    // Creates the built-in arrangements (from the screen shapes and positions at startup)
    static void BuiltinArrangements(Workspace w)
    {
        Arrangements.Clear();
        savedArrangements.Clear();
        arrangementsLoaded = false;
        // Overlay: the game image fills the display, and the inspector and code panels are overlaid translucently on the code screen in front
        var overlay = Arrangement.Capture(Overlay, w);
        // Side by side: the game image on the left, the code panels and inspector stacked on the right
        var side = Arrangement.Capture(SideBySide, w);
        foreach (var a in new List<Arrangement> { overlay, side })
            foreach (var spec in a.Screens)
            {
                spec.Windows.Clear();
                var tiled = a == side;
                if (spec.Name == backdrop.Name)
                {
                    // The side-by-side arrangement is placed in the world (in XR it does not follow the head, and you can turn your head to see to the edges)
                    if (tiled)
                        spec.Anchor = ScreenAnchor.World;
                    spec.Layout = tiled ? "MainStack mains=1" : "Even";
                    spec.Windows.Add("game:saved");
                    spec.Windows.Add("game:draft");
                    if (tiled)
                    {
                        spec.Windows.Add(Arrangement.AllCode);
                        spec.Windows.Add("inspector");
                    }
                }
                else if (spec.Name == w.CodeScreen.Name && !tiled)
                {
                    spec.Windows.Add("inspector");
                    spec.Windows.Add(Arrangement.AllCode);
                }
            }
        Arrangements.Add(side);
        Arrangements.Add(overlay);
        ArrangementName = Overlay;
        RegisterArrangements();
    }

    // Registers, for each arrangement, a command that restores it
    static void RegisterArrangements()
    {
        foreach (var a in Arrangements)
        {
            var name = a.Name;
            CommandTable.Register("arrange:" + name, "配置「" + name + "」に戻す", () => With(w => Arrange(name)));
        }
    }

    // Reads the file of recorded arrangements once, when it becomes readable. Arrangements with the same name are replaced by the recorded ones
    // Key bindings: users' changes are written to a per-user file (independent of the project).
    // The file is env LIDER_KEYS, or if missing, lider/keys.txt in XDG_CONFIG_HOME or ~/.config. If no location can be determined, changes last until lider exits
    static bool keysLoaded;
    // Command that the next pressed key is bound to (null if not waiting)
    static string? keyFor;
    public static string? KeyFor => keyFor;

    static string? KeysPath()
    {
        var path = Environment.GetEnvironmentVariable("LIDER_KEYS");
        if (path != null && path != "")
            return path;
        var dir = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (dir == null || dir == "")
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (home == null || home == "")
                return null;
            dir = home + "/.config";
        }
        return dir + "/lider/keys.txt";
    }

    static void LoadKeys()
    {
        if (keysLoaded)
            return;
        var path = KeysPath();
        if (path == null)
        {
            keysLoaded = true;
            return;
        }
        Io.LoadText(path, out var text, out _, out var status, out _);
        if (status == Io.Status.Pending)
            return;
        keysLoaded = true;
        if (text == null)
            return;
        Keymap.Load(text);
        // At least one key that brings focus back from the game is always needed
        if (Keymap.StrokeOf("focus:game") == "")
            Keymap.Bind("f1", "focus:game");
    }

    static void SaveKeys()
    {
        var path = KeysPath();
        if (path != null)
            Io.SaveText(path, string.Join("\n", Keymap.Lines()) + "\n");
    }

    static string KeyPrompt(string name)
    {
        var now = Keymap.ShownOf(name);
        return name + " の key を押す (今は " + (now != "" ? now : "無し") + "。Esc でやめる)";
    }

    // Binds the key pressed while waiting to the command keyFor. Esc cancels. For keys that cannot be bound, shows the reason and keeps waiting
    static void TakeKey(Workspace w, EventData e)
    {
        var name = keyFor!;
        var key = EditorHost.NameOf(e.Key, e.Plain);
        if (e.Key == Keys.Escape || key == null)
        {
            keyFor = null;
            Search.Cancel(w);
            return;
        }
        var stroke = Keymap.Stroke(key);
        if (!Keymap.Assignable(stroke))
        {
            Search.Prompt = Keymap.Shown(stroke) + " は割り当てられない (Ctrl か Alt を付けた key か、F key)";
            return;
        }
        // Do not let other commands take the key that brings focus back from the game
        if (name != "focus:game" && Keymap.Binds(key, "focus:game"))
        {
            Search.Prompt = Keymap.Shown(stroke) + " は focus:game の key なので割り当てられない";
            return;
        }
        Keymap.Bind(stroke, name);
        SaveKeys();
        keyFor = null;
        Search.Cancel(w);
    }

    static void LoadArrangements(Workspace w)
    {
        if (arrangementsLoaded)
            return;
        Io.LoadText(ArrangementsPath(w), out var text, out _, out var status, out _);
        if (status == Io.Status.Pending)
            return;
        arrangementsLoaded = true;
        if (text == null)
            return;
        foreach (var a in Arrangement.Read(text))
            Keep(a);
        RegisterArrangements();
    }

    // Adds recorded arrangements to the list (ones with the same name are replaced)
    static void Keep(Arrangement a)
    {
        Replace(savedArrangements, a);
        Replace(Arrangements, a);
    }

    static void Replace(List<Arrangement> list, Arrangement a)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i].Name == a.Name)
            {
                list[i] = a;
                return;
            }
        list.Add(a);
    }

    // Restores the named arrangement (false if missing)
    public static bool Arrange(string name)
    {
        var w = Work;
        if (w == null)
            return false;
        foreach (var a in Arrangements)
        {
            if (a.Name != name)
                continue;
            // The shown inspector is also placed where the arrangement decides
            PlaceInspector(w);
            a.Apply(w);
            ArrangementName = name;
            // Reset the sideways view slide when the arrangement changes
            viewTarget = 0;
            viewX.Snap(0);
            followed = null;
            // At the new place, it grows from a small size, the same as when opened
            motions.Clear();
            PlaceInspector(w);
            return true;
        }
        return false;
    }

    // Restores the arrangement after the current one in the list
    public static void NextArrangement()
    {
        var at = 0;
        for (var i = 0; i < Arrangements.Count; i++)
            if (Arrangements[i].Name == ArrangementName)
                at = i;
        if (Arrangements.Count > 0)
            Arrange(Arrangements[(at + 1) % Arrangements.Count].Name);
    }

    // Records the current arrangement as name and writes it to the file (overwrites an arrangement with the same name)
    public static void SaveArrangement(string name)
    {
        var w = Work;
        if (w == null)
            return;
        Keep(Arrangement.Capture(name, w));
        ArrangementName = name;
        var lines = new List<string>();
        foreach (var a in savedArrangements)
            foreach (var line in a.Lines())
                lines.Add(line);
        Io.SaveText(ArrangementsPath(w), string.Join("\n", lines) + "\n");
        RegisterArrangements();
    }

    // Game states recorded with names (the list index is the record number). Records last until lider is closed
    static readonly List<string> savedStates = new List<string>();
    // Start of the record numbers (a range that does not overlap the recorded frames or the record numbers copied to the draft version)
    const int StateSlots = 2000000;
    // Reason a state could not be recorded or restored, or a value could not be written (shown in the focused window; cleared the next time it succeeds)
    public static string StateNotice = "";

    // Records the game state of the version lider is looking at as name (overwrites if the name exists). Each time, registers the command state:load:<name> that restores it
    public static void SaveState(string name)
    {
        if (name == "")
            return;
        var at = savedStates.IndexOf(name);
        if (at < 0)
        {
            at = savedStates.Count;
            savedStates.Add(name);
            CommandTable.Register("state:load:" + name, "状態「" + name + "」に戻す", () => With(w => LoadState(name)));
        }
        StateNotice = Watched.Save(StateSlots + at, out var error) ? "" : "状態「" + name + "」を控えられません: " + error;
    }

    // Whether it is an inspector value row that can be rewritten while paused (expand/collapse rows, method rows, and separator rows cannot be written)
    static bool Editable(InspectRow row)
    {
        return Paused && !row.Openable && row.Text != "";
    }

    // A value was pressed while paused: a bool is toggled; for anything else, the user types a new value in the search field
    public static void EditValue(Workspace w, string path, string text)
    {
        if (!Paused)
            return;
        if (text == "true" || text == "false")
            SetValue(path, text == "true" ? "false" : "true");
        else
            Search.Ask(w, path + " (今 " + text + ") =", typed => SetValue(path, typed));
    }

    // Rewrites the value at path in the game of the version lider is looking at to the value of text (only while paused). If it cannot be written, reports the reason
    public static void SetValue(string path, string text)
    {
        StateNotice = Watched.Set(path, text, out var error) ? "" : path + " に書けません: " + error;
    }

    // Loads the state recorded as name into the game of the version lider is looking at. If it cannot be loaded, the game keeps running as is and the reason is reported
    public static void LoadState(string name)
    {
        var at = savedStates.IndexOf(name);
        if (at < 0)
            return;
        StateNotice = Watched.Load(StateSlots + at, out var error) ? "" : "状態「" + name + "」に戻せません: " + error;
    }

    // Moves window to the next screen (cycles through the screens that lay out windows).
    // Screens whose rule stacks from an edge (hints, search) are for showing fixed things, so they are not move targets
    public static void MoveToNextScreen(Workspace w, Window window)
    {
        var on = w.ScreenOf(window);
        if (on == null)
            return;
        var at = Screens.IndexOf(on);
        for (var i = 1; i < Screens.Count; i++)
        {
            var next = Screens[(at + i) % Screens.Count];
            if (next.Layout is Stack)
                continue;
            w.MoveTo(window, next);
            // At the new place, it grows from a small size, the same as when opened
            motions.Remove(window);
            if (window.Content is Code)
                w.Home = next == w.CodeScreen ? null : next;
            return;
        }
    }

    // Fits the panel size to Zen. The px for one panel to fit on the display is decided by the panel's px size (scale) and the field of view
    static void FitWindows()
    {
        var w = Work;
        if (w == null)
            return;
        foreach (var window in w.Windows)
        {
            window.PxW = window.WidthPx;
            window.PxH = window.HeightPx;
            if (ZenShown && window == w.FocusWindow)
            {
                if (lastEye != null)
                    window.PxW = PanelView.BasePxW * 3 / 2;
                else
                {
                    Gfx.Size(out var sw, out var sh);
                    var half = w.CodeScreen.Radius * (float)Math.Tan(fov * 0.5f * Math.PI / 180);
                    var perPx = PanelView.BaseW / PanelView.BasePxW * window.Scale;
                    window.PxW = Math.Max(window.PxW, (int)(2 * half * sw / sh * ZenFill / perPx));
                    window.PxH = Math.Max(window.PxH, (int)(2 * half * ZenFill / perPx) - PanelView.TitlePx);
                }
            }
            // On flat screens, fit the title and body exactly into the rectangle decided by the arrangement rule
            var screen = ScreenFor(w, window);
            if (OnPlane(screen) && window.Position != null)
            {
                var perPx = PanelView.BaseW / PanelView.BasePxW * window.Scale / ViewScale(screen);
                // To avoid overflowing the rectangle, the user-set minimum size (Window.MinPxW) is not applied
                window.PxW = Math.Max(1, (int)(window.Size.X / perPx));
                window.PxH = Math.Max(1, (int)(window.Size.Y / perPx) - PanelView.TitlePx);
            }
            var panel = Panel(window);
            panel.PxW = window.PxW;
            panel.PxH = window.PxH;
        }
    }

    // Highlights the edge being pointed at or dragged (world is the view-projection in world coordinates)
    static void DrawEdge(Workspace w, Mat4 world)
    {
        var window = sizing ?? edgeHoverWindow;
        var edge = sizing != null ? sizingEdge : edgeHover;
        if (window == null || window.Position == null)
            return;
        var screen = ScreenFor(w, window);
        var color = Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.9f);
        var at = window.Position.Value;
        var line = 0.005f / Apparent(screen);
        if ((edge & 1) != 0)
            SurfaceQuad(screen, world, new Vec4(at.X + window.Size.X * 0.5f + line, at.Y, line, window.Size.Y), color, null);
        if ((edge & 2) != 0)
            SurfaceQuad(screen, world, new Vec4(at.X, at.Y - window.Size.Y * 0.5f - line, window.Size.X, line), color, null);
    }

    // Width of the part of a window edge that is dragged to resize (meters; apparent length at radius Space.R; outside the window)
    const float EdgeW = 0.018f;

    // Edge of the pointed window. Sum of 1 for right and 2 for bottom (0 if none is hit).
    // Only edges that the rule of the window's screen allows can be dragged
    public static int EdgeAt(Workspace w, Window window, Ray ray)
    {
        var screen = ScreenFor(w, window);
        if (grab.Holding || window.Position == null || !screen.Shown)
            return 0;
        var can = screen.Layout.Edges(screen, window);
        var p = can != 0 ? SurfacePoint(screen, ray) : null;
        if (p == null)
            return 0;
        var band = EdgeW / Apparent(screen);
        var x = p.Value.X - (window.Position.Value.X + window.Size.X * 0.5f);
        var y = window.Position.Value.Y - window.Size.Y * 0.5f - p.Value.Y;
        // Outside the right edge and outside the bottom edge (both at the corner)
        if (x < -window.Size.X || x >= band || y < -window.Size.Y || y >= band)
            return 0;
        return ((can & 1) != 0 && x >= 0 ? 1 : 0) + ((can & 2) != 0 && y >= 0 ? 2 : 0);
    }

    // Finds an outer right or bottom edge of a window (the focused window is checked first). Puts the edge direction in foundEdge
    static Window? EdgeWindow(Workspace w, Ray ray)
    {
        foundEdge = 0;
        if (w.Focused != null)
        {
            foundEdge = EdgeAt(w, w.Focused, ray);
            if (foundEdge != 0)
                return w.Focused;
        }
        foreach (var screen in Screens)
            foreach (var window in screen.Windows)
            {
                foundEdge = EdgeAt(w, window, ray);
                if (foundEdge != 0)
                    return window;
            }
        return null;
    }

    // Moves the dragged edge to where the ray points (the screen rule decides which borders move and how)
    public static void DragEdge(Workspace w, Ray ray)
    {
        var window = sizing;
        if (window == null || Zen || window.Position == null)
            return;
        var screen = ScreenFor(w, window);
        var p = SurfacePoint(screen, ray);
        if (p == null)
            return;
        // The edge does not necessarily move by as much as the size grew (a non-moving window in the middle expands to both sides), so repeat until the edge comes close to the pointed place
        for (var i = 0; i < 4 && window.Position != null; i++)
        {
            var right = window.Position.Value.X + window.Size.X * 0.5f;
            var bottom = window.Position.Value.Y - window.Size.Y * 0.5f;
            screen.Layout.Grow(screen, window, (sizingEdge & 1) != 0 ? p.Value.X - right : 0, (sizingEdge & 2) != 0 ? bottom - p.Value.Y : 0);
            screen.Arrange();
            // For rules where the edge does not move even after relayout (the size is decided on the next frame), the next drag continues bringing it closer
            if (window.Position == null || (window.Position.Value.X + window.Size.X * 0.5f == right && window.Position.Value.Y - window.Size.Y * 0.5f == bottom))
                break;
        }
    }

    // Grows the focused window's width by dx and its height by dy meters (apparent length at radius Space.R)
    static void GrowFocused(Workspace w, float dx, float dy)
    {
        var window = w.Focused;
        if (window == null || window.Position == null)
            return;
        var screen = ScreenFor(w, window);
        screen.Layout.Grow(screen, window, dx / Apparent(screen), dy / Apparent(screen));
        // A Code window is lifted slightly while it is moved by keys
        if (window == w.FocusWindow)
        {
            keyMoved = window;
            keyLift = 0.3f;
        }
    }

    // Notifies the rule of the screen that holds the focused window
    static void TellLayout(Workspace w, string message)
    {
        var window = w.Focused ?? w.FocusWindow;
        if (window != null)
            ScreenFor(w, window).Layout.Tell(message);
    }

    public static void StartEdge(Window window, int edge)
    {
        sizing = window;
        sizingEdge = edge;
    }

    public static void EndEdge()
    {
        sizing = null;
    }

    static Window? sizing;
    static int sizingEdge;
    static int edgeHover;
    static int foundEdge;
    static Window? edgeHoverWindow;

    // Fraction of the display taken by a Zen panel
    const float ZenFill = 0.96f;

    static void Frame(float dt)
    {
        FitWindows();
        DeclareGap.Frame++;
        var w = Work;
        if (w == null)
            return;
        Space.Flat = !Xr.Active();
        LoadArrangements(w);
        LoadKeys();
        // When the search field is closed, also stop waiting for a key
        if (!Search.Shown)
            keyFor = null;
        PlaceInspector(w);
        // Records are received and discarded even while lider is not shown (not accumulated)
        if (inspect.Tree.ScopeId != tracedScope)
        {
            tracedScope = inspect.Tree.ScopeId;
            w.Trace.ForgetLines();
        }
        w.Trace.Take(tracedScope != "" ? InspectTree.ScopePin : "");
        Costs.Refresh();
        Costs.Viewing = inspect.Shown && inspect.Load;
        // While viewing the profile, show load numbers in place of line numbers (line numbers are not drawn, and the column is wide enough for 4 digits)
        var numbersOff = Costs.Enabled && Costs.Viewing;
        DocView.LineNumbers = !numbersOff;
        DocView.MinNumberWidth = numbersOff && text != null ? PanelView.NumberCells * PanelText().CellW : 0;
        LogFocus(w);
        if (!LiderShown(w) || !Quad3d.Ensure())
            return;
        PanelView.DeclareShaders();
        Opacity = Ease.Toward(Opacity, w.OnGame ? GameOpacity : 1, dt, 0.25f);
        if (text == null)
        {
            text = new MonoText("lider_mono", Ttf(), 15);
            // Match the editor width and line height to lider's columns
            Document.Boot(text.CellW, text.LineH);
            Session.ScrollTops();
        }
        var panelText = PanelText();
        w.Tick(dt);
        if (w.FocusWindow != null)
            EditorHost.Focus(w.FocusWindow.Code.ViewId);
        EditorHost.Step();
        w.Sync();
        TakePick(w);

        Cues.Declare();
        Fields.Refresh();
        FollowScope();
        // Collect the field declarations of the panels and the method declarations of the inspector
        if (fieldFrames++ % ScanFrames == 0)
            Search.Refresh(w);
        // Texture redraws are done outside the pass to the display
        foreach (var window in w.Windows)
        {
            if (Space.Flat && !window.Sized)
            {
                window.Sized = true;
                window.Scale = Space.FlatScale;
            }
            var panel = Panel(window);
            panel.Errors = w.ErrorsIn(window.Code.Doc.Path);
            panel.Notices = InitNotices(w, window);
            panel.ErrorsVersion = w.ErrorsVersion;
            panel.Costs = Costs;
            panel.Fields = Fields.For(Search.DeclarationsIn(window.Code.Doc.Path), EditorHost.FirstLine(window.Code.ViewId), EditorHost.LastLine(window.Code.ViewId));
            var differs = window == w.FocusWindow && window.Code.Doc.Differs();
            panel.OldShown = differs && PanelView.SideBySide && !ZenShown;
            PlaceDiff(window, panel.OldShown);
            panel.Inline = differs && !PanelView.SideBySide;
            EditorHost.SetGaps(window.Code.ViewId, panel.Inline ? window.Code.Doc.InlineGaps() : NoGaps);
            panel.Prepare(window.Code.Doc, panelText, w.Trace);
        }
        w.Arrange(grab.Window);
        // Panels in their closing animation are not redrawn, but they are drawn, so keep declaring the texture
        foreach (var pair in motions)
            if (!w.Windows.Contains(pair.Key))
                Panel(pair.Key).Declare(panelText);
        // Keep the atlas even for text other than the panel body text, since hints and inspect use it
        text.Glyphs.Ensure();
        if (Search.Shown || inspect.Shown)
            Search.Refresh(w);
        inspect.Prepare(panelText, w, Search, Fields, Costs);
        PrepareTitles(w, panelText);
        if (Search.Shown)
            searchView.Prepare(Search, panelText);
        PrepareScreenTags(w, panelText);
        PlaceOsk(w);
        if (osk.Shown)
            oskView.Prepare(osk, panelText);
        AssetBoard.Prepare(w.ShownAsset, w.ShownAsset == "" ? "" : w.Relative(w.ShownAsset), panelText);
        PlacePalette(w);
        Animate(w, dt);

        if (Xr.Active())
        {
            var left = Xr.View(0, 0.05f, 100);
            var right = Xr.View(1, 0.05f, 100);
            if (left == null || right == null)
                return;
            if (!anchor.IsSet())
                anchor.Recenter(left, right);
            lastEye = left;
            gaze = Gaze(left, right);
            headToWorld = HeadToWorld(left, right);
            // The pointing ray is the first available of: the pressing hand, the right hand, the left hand
            var aim = (activeHand >= 0 ? HandRay(activeHand) : null) ?? HandRay(1) ?? HandRay(0) ?? gaze;
            for (var h = 0; h < 2; h++)
                AimAt(h, HandRay(h));
            // The left hand's Y toggles the on-screen keyboard
            var leftHand = Xr.Input(0);
            var y = leftHand != null && leftHand.Secondary;
            if (y && !oskButton)
                osk.Shown = !osk.Shown;
            oskButton = y;
            // On the on-screen keyboard, the key each hand points at lights up
            for (var h = 0; h < 2; h++)
                PointOsk(w, h, HandRay(h));
            Triggers(w, w.OnGame);
            if (!w.OnGame)
            {
                if (!Search.Shown)
                    w.Look(WindowAt(w, gaze)?.Window, dt);
                Controllers(w, aim, dt);
                ShowHints(w, aim);
            }
            else
                HideHints();
            DrawEye(w, left);
            DrawEye(w, right);
            return;
        }
        lastEye = null;

        Gfx.Size(out var sw, out var sh);
        // The sideways view slide and the width that fits on the display are counted in meters on the code screen surface
        var depth = w.CodeScreen.Radius;
        var halfViewportH = depth * (float)Math.Tan(fov * 0.5f * Math.PI / 180);
        Input.MousePos(out var mx, out var my);
        var forGame = MouseForGame(w, mx, my);
        if (!forGame && Input.MouseDown(3) && !grab.Holding && heldScreen == null)
        {
            Input.MouseDelta(out var dx, out _);
            viewTarget -= dx * 2 * halfViewportH / viewportH;
            viewX.Snap(viewTarget);
        }
        var pxPerM = viewportH / (2 * halfViewportH);
        if (AreaW > 0)
            FitArea(w, pxPerM);
        else
            FollowFocus(w, halfViewportH * viewportW / viewportH);
        viewX.Step(viewTarget, dt);
        // Moving the view causes parallax between layers, so the view stays at the origin and things are shifted sideways in proportion to depth (every layer shifts by the same angle).
        // If an area is set, align vertically with the middle of the area too
        var slide = Mat4.Identity();
        slide.M02 = -viewX.Value / depth;
        if (AreaW > 0)
        {
            // The panel title sits above the body, so align the middle of the height including the title (height TitleH / 2 on the surface) with the middle of the area
            slide.M12 = ((viewportH * 0.5f - AreaY - AreaH * 0.5f) / pxPerM - AreaCenterY - w.CodeScreen.Position.Y) / depth;
        }
        lastVp = Camera3d.Vp(new Camera3dOpts
        {
            Eye = new Vec3(0, 0, 0),
            Target = new Vec3(0, 0, 1),
            Fov = fov,
            Near = 0.05f,
            Far = 100,
            Aspect = (float)viewportW / viewportH,
        }) * slide;
        cameraSet = true;
        PointOsk(w, 0, null);
        PointOsk(w, 1, PointerRay(mx, my));
        if (!forGame)
        {
            Hover(w, PointerRay(mx, my), mx, my, dt);
            ShowHints(w, PointerRay(mx, my));
        }
        else
        {
            HideHints();
            ShowGameHints(w);
        }
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        DrawScene(w, lastVp);
        Gfx.EndPass();
    }

    // Sets the display area where the inspector and the focused panel are placed (if w is 0, no area is set)
    public static void SetArea(float x, float y, float w, float h)
    {
        AreaX = x;
        AreaY = y;
        AreaW = w;
        AreaH = h;
    }

    // Decides the inspector and panel sizes and the view so they fit in the area.
    // pxPerM is the pixels per 1 meter on the surface (radius R). The inspector is in front (InspectR), so it looks R / InspectR times larger
    static void FitArea(Workspace w, float pxPerM)
    {
        // The inspector goes at the left of the area, at the same height as the panel body
        InspectView.SetSize(AreaW * AreaBoard / pxPerM / InspectScale, (AreaH / pxPerM - PanelView.TitleH * Space.FlatScale) / InspectScale);
        var gap = Strip.Gap * pxPerM;
        foreach (var window in w.Windows)
        {
            var pxW = (int)((AreaW * (1 - AreaBoard) - gap) / pxPerM / window.Scale / PanelView.BaseW * PanelView.BasePxW);
            var pxH = (int)((AreaH / pxPerM / window.Scale - PanelView.TitleH) / PanelView.BaseW * PanelView.BasePxW);
            if (window.WidthPx != pxW || window.HeightPx != pxH)
                window.Resize(pxW, pxH);
        }
        var focus = w.FocusWindow;
        if (focus == null)
        {
            // If there is no panel, align the inspector's left edge with the area's left edge
            if (TreeVisible)
            {
                viewTarget = BoardAngle * Space.R - InspectSize().X * 0.5f - (AreaX - viewportW * 0.5f) / pxPerM;
                viewX.Snap(viewTarget);
            }
            return;
        }
        if (grab.Holding)
            return;
        // Align the panel's right edge with the area's right edge
        var right = WindowAngle(w, focus) * Space.R + focus.UnscaledSize.X * focus.Scale * 0.5f;
        viewTarget = right - (AreaX + AreaW - viewportW * 0.5f) / pxPerM;
    }

    // When focus moves or a panel moves, move the view target until the focused panel is visible (after the row is moved, it does not follow until the next move)
    static void FollowFocus(Workspace w, float halfViewport)
    {
        var window = w.FocusWindow;
        // If there are no code panels, move the inspector to the left edge of the display
        if (window == null && TreeVisible && ScreenFor(w, inspectWindow) == w.CodeScreen)
            viewTarget = BoardAngle * Space.R - InspectSize().X * 0.5f - FollowMargin + halfViewport;
        if (window == null || grab.Holding || ScreenFor(w, window) != w.CodeScreen)
            return;
        var angle = WindowAngle(w, window);
        // If the inspector is lined up right to the left of the focused panel, move the view so the inspector is visible too (its left edge; 0 if not lined up)
        var row = w.CodeScreen.Windows;
        var beside = TreeVisible && row.IndexOf(inspectWindow) >= 0 && row.IndexOf(inspectWindow) == row.IndexOf(window) - 1;
        var board = beside ? BoardAngle * Space.R - InspectSize().X * 0.5f : 0;
        if (window == followed && angle == followedAngle && window.Scale == followedScale && board == followedBoard && OldBoardW == followedOld)
            return;
        followed = window;
        followedAngle = angle;
        followedScale = window.Scale;
        followedBoard = board;
        followedOld = OldBoardW;
        var half = window.UnscaledSize.X * window.Scale * 0.5f;
        // Include the running-version board to the left of the panel
        var left = beside ? Math.Min(board, angle * Space.R - half - OldBoardW) : angle * Space.R - half - OldBoardW;
        // If the board and the panel do not both fit, show the board side (the right end of the panel has less text)
        var right = Math.Min(angle * Space.R + half, left + 2 * (halfViewport - FollowMargin));
        viewTarget = Space.FollowX(viewTarget, (left + right) * 0.5f, (right - left) * 0.5f + FollowMargin, halfViewport);
    }

    // Width left outside the panel when the view follows it (meters)
    const float FollowMargin = 0.03f;

    // Height of the floor where panels cast light, and distance from the cylinder axis (the origin is the midpoint of both eyes after Recenter; the floor height is provisional)
    const float FloorY = -1.1f;
    const float FloorR = Space.R * 0.8f;

    // Recorded-frames bar: part of the game image window, shown over the bottom edge of the image only while paused (the image is not shrunk).
    // Bar height, width and height of the paused-position mark, and gap from the image edge (screen coordinates)
    const float TimelineH = 4;
    const float TimelineMarkW = 3;
    const float TimelineMarkH = 14;
    const float TimelineMargin = 12;

    // Position on the bar of the at-th recorded frame (0 is the latest) (the bar width is 1; the middle is 0, the right end is the latest)
    static float TimelineX(int at, int count)
    {
        return 0.5f - (at + 0.5f) / count;
    }

    // Rectangle where the bar is shown (including the mark height; null if not shown).
    // Placed at the bottom edge of the saved version's game image. If an area (LIDER_AREA) is set, at the bottom edge of the part of the image inside the area
    static PixelRect? TimelineRect()
    {
        var at = gameWindow.ContentRect;
        if (!timelineShown || at == null || GameApp.Count == 0)
            return null;
        var x0 = at.X;
        var x1 = at.X + at.W;
        var y1 = at.Y + at.H;
        if (AreaW > 0)
        {
            x0 = Math.Max(x0, AreaX);
            x1 = Math.Min(x1, AreaX + AreaW);
            y1 = Math.Min(y1, AreaY + AreaH);
        }
        return new PixelRect(x0 + TimelineMargin, y1 - TimelineMargin - TimelineMarkH, x1 - x0 - 2 * TimelineMargin, TimelineMarkH);
    }

    static Spring Stiff()
    {
        var s = new Spring(0);
        s.Stiffness = 900;
        s.Damping = 60;
        return s;
    }

    // While paused, show the bar and slide the paused-position mark to the current frame (even while lider is hidden)
    static void StepTimeline(float dt)
    {
        if (GameApp.Position >= 0 && GameApp.Count > 0)
        {
            var x = TimelineX(GameApp.Position, GameApp.Count);
            if (!timelineShown)
                timeline.Snap(x);
            timeline.Step(x, dt);
            timelineShown = true;
        }
        else if (stepping == 0)
        {
            timelineShown = false;
        }
    }

    // Draws the bar and the paused-position mark over the game image window (call inside the labelBatch pass)
    static void DrawTimeline()
    {
        var band = TimelineRect();
        if (band == null || band.W <= 0)
            return;
        var y = band.Y + band.H * 0.5f;
        labelBatch!.Rect(band.X, y - TimelineH * 0.5f, band.W, TimelineH, Color.Rgb(0.3f, 0.35f, 0.45f, 0.6f));
        // The light dot slides to the next frame each time a frame is stepped
        labelBatch.Rect(band.X + (0.5f + timeline.Value) * band.W - TimelineMarkW * 0.5f, band.Y, TimelineMarkW, TimelineMarkH, Color.Rgb(1.0f, 0.6f, 0.15f, 1));
    }

    // Matrix from head coordinates (origin at the midpoint of both eyes, +Z forward) to lider's space
    static Mat4 HeadToWorld(XrView left, XrView right)
    {
        var o = left.Orientation;
        var pos = new Vec3((left.Position[0] + right.Position[0]) * 0.5f, (left.Position[1] + right.Position[1]) * 0.5f,
            (left.Position[2] + right.Position[2]) * 0.5f);
        var m = anchor.ToLocal();
        var values = new List<float>();
        for (var i = 0; i < 16; i++)
            values.Add(m[i]);
        var flip = Mat4.Scale(new Vec3(1, 1, -1));
        return flip * Mat4.FromList(values, 0).Inverse() * Mat4.Translate(pos) * Mat4.FromQuat(new Quat(o[0], o[1], o[2], o[3])) * flip;
    }

    // Turns the head direction (from the midpoint of both eyes, forward along the left eye's direction) into a ray in lider's space
    static Ray Gaze(XrView left, XrView right)
    {
        var o = left.Orientation;
        var dir = new Quat(o[0], o[1], o[2], o[3]).RotateVec3(new Vec3(0, 0, -1));
        var pos = new Vec3((left.Position[0] + right.Position[0]) * 0.5f, (left.Position[1] + right.Position[1]) * 0.5f,
            (left.Position[2] + right.Position[2]) * 0.5f);
        return FromLocal(pos, dir);
    }

    // Turns a hand's (0 left, 1 right) aim into a ray in lider's space (null if the pose is not available)
    static Ray? HandRay(int hand)
    {
        var h = Xr.Input(hand);
        if (h == null || !h.PoseValid)
            return null;
        var o = h.AimOrientation;
        var dir = new Quat(o[0], o[1], o[2], o[3]).RotateVec3(new Vec3(0, 0, -1));
        return FromLocal(new Vec3(h.AimPosition[0], h.AimPosition[1], h.AimPosition[2]), dir);
    }

    // Converts the hand's grip position into lider's space (null if the pose is not available)
    static Vec3? GripPoint(int hand)
    {
        var h = Xr.Input(hand);
        if (h == null || !h.PoseValid)
            return null;
        return FromLocal(new Vec3(h.GripPosition[0], h.GripPosition[1], h.GripPosition[2]), new Vec3(0, 0, -1)).Origin;
    }

    // Triggers of both hands: press where pointing, and drag by moving while pulled. Only one hand at a time can press and drag; it continues until the hand that pulled first releases.
    // With oskOnly (while focus is on the game), only the on-screen keyboard is pressed (so the key that brings focus back can be typed)
    static void Triggers(Workspace w, bool oskOnly)
    {
        for (var h = 1; h >= 0; h--)
        {
            var input = Xr.Input(h);
            var ray = HandRay(h);
            // Press and release use separate thresholds so jitter does not repeat them
            var down = input != null && ray != null && (triggerDown[h] ? input.Trigger > 0.3f : input.Trigger > 0.7f);
            if (down && !triggerDown[h] && oskOnly)
                PressOsk(w, ray!.Value, h);
            else if (down && !triggerDown[h] && activeHand < 0)
            {
                activeHand = h;
                // On the on-screen keyboard, press the key the pressing hand points at
                if (!PressOsk(w, ray!.Value, h))
                    Click(w, ray!.Value, true);
            }
            else if (down && activeHand == h && !oskOnly)
                DragTo(w, ray!.Value);
            else if (!down && activeHand == h)
            {
                ReleasePress(w);
                activeHand = -1;
            }
            triggerDown[h] = down;
        }
    }

    // Decides the surface and position that the ray of hand hand points at.
    // Checking from the nearest screen: if it hits a window, that point; if it hits no window, the farthest surface hit
    static void AimAt(int hand, Ray? ray)
    {
        aimScreen[hand] = null;
        if (ray == null)
            return;
        var onWindow = false;
        foreach (var screen in Screens)
        {
            if (!screen.Shown)
                continue;
            var at = SurfacePoint(screen, ray.Value);
            if (at == null)
                continue;
            var hit = false;
            foreach (var window in screen.Windows)
                if (window.Position != null && Math.Abs(at.Value.X - window.Position.Value.X) <= window.Size.X * 0.5f
                    && Math.Abs(at.Value.Y - window.Position.Value.Y) <= window.Size.Y * 0.5f)
                    hit = true;
            var best = aimScreen[hand];
            var better = best == null || (hit && !onWindow)
                || (hit == onWindow && (hit ? screen.Distance < best.Distance : screen.Distance > best.Distance));
            if (!better)
                continue;
            aimScreen[hand] = screen;
            aimAt[hand] = at.Value;
            onWindow = hit;
        }
    }

    static void Controllers(Workspace w, Ray aim, float dt)
    {
        var right = Xr.Input(1);
        var left = Xr.Input(0);
        pointer = Space.OnCylinderAt(RayOn(w.CodeScreen, aim), Space.R);

        var rGrip = right != null && (rightGrip ? right.Grip > 0.3f : right.Grip > 0.7f);
        var lGrip = left != null && (leftGrip ? left.Grip > 0.3f : left.Grip > 0.7f);
        if (rGrip && !rightGrip && pointer != null)
        {
            var hit = WindowAt(w, aim);
            if (hit != null)
                w.FocusWindow = hit.Window;
            if (hit != null && ScreenFor(w, hit.Window) == w.CodeScreen)
                grab.Start(hit.Window, w.CodeScreen.XAt(pointer.Angle), pointer.Y);
        }
        if (!rGrip && rightGrip)
            grab.Release();
        if (grab.Holding)
        {
            var a = GripPoint(0);
            var b = GripPoint(1);
            if (lGrip && !leftGrip && a != null && b != null)
                grab.StartTwo(a.Value, b.Value);
            else if (!lGrip && leftGrip && pointer != null)
                grab.ReleaseTwo(w.CodeScreen.XAt(pointer.Angle), pointer.Y);
            if (lGrip && a != null && b != null)
                grab.MoveTwo(a.Value, b.Value);
            else if (pointer != null)
                grab.Move(w.CodeScreen.XAt(pointer.Angle), pointer.Y);
        }
        rightGrip = rGrip;
        leftGrip = lGrip;

        // The left hand's X toggles the inspector
        var x = left != null && left.Primary;
        if (x && !inspectButton)
            inspect.Shown = !inspect.Shown;
        inspectButton = x;
        // The right stick scrolls the inspector if it is pointed at, otherwise the focused panel, at up to 6 notches per second
        if (right != null && Math.Abs(right.StickY) > 0.2f && InspectAt(aim) != null)
            inspect.Scroll -= right.StickY * dt * 18;
        else if (right != null && w.FocusWindow != null && Math.Abs(right.StickY) > 0.2f)
            EditorHost.Scroll(w.FocusWindow.Code.ViewId, right.StickY * dt * 6);
        // While paused, tilting the left stick steps frames (every 0.12 seconds while held)
        stepWait -= dt;
        if (left != null && GameApp.Position >= 0 && Math.Abs(left.StickX) > 0.6f && stepWait <= 0)
        {
            if (left.StickX < 0)
                StepBack();
            else
                StepForward();
            stepWait = 0.12f;
        }
        if (left == null || Math.Abs(left.StickX) <= 0.6f)
            stepWait = 0;
    }

    // Panel visuals (null if not drawn yet)
    public static WindowMotion? MotionOf(Window window)
    {
        return motions.TryGetValue(window, out var m) ? m : null;
    }

    // Moves the panel visuals toward their targets and gives feedback for save, reload, typing, and focus moves
    public static void Animate(Workspace w, float dt)
    {
        var celebrate = w.Fixed != seenFixed;
        seenFixed = w.Fixed;
        var now = Sys.Seconds();
        var repeat = now - LastLandAt < RepeatSeconds;
        if (w.Jumps != seenJumps)
        {
            seenJumps = w.Jumps;
            if (w.FocusWindow != null && motions.TryGetValue(w.FocusWindow, out var arrived))
                arrived.Arrive = Juice > 0 ? 1 : 0;
            Cues.Play(Cues.Move, 0.25f);
        }
        foreach (var window in w.Windows)
        {
            if (!motions.TryGetValue(window, out var m))
            {
                // An opened panel grows in place from a small size
                m = new WindowMotion(WindowAngle(w, window), WindowY(w, window), WindowMotion.SmallScale);
                m.SeenSaved = window.Code.Doc.Saved;
                m.SeenLanded = window.Code.Doc.Landed;
                m.SeenRevision = window.Code.Doc.Revision;
                m.SeenFailures = w.Failures;
                m.SeenRemoved = EditorHost.RemovedCount(window.Code.ViewId);
                motions[window] = m;
            }
            if (m.SeenDoc != window.Code.Doc)
            {
                // When the tab changes, track from the current state of that file (so the switch is not mistaken for a save or an apply)
                m.SeenDoc = window.Code.Doc;
                m.SeenSaved = window.Code.Doc.Saved;
                m.SeenLanded = window.Code.Doc.Landed;
                m.SeenRevision = window.Code.Doc.Revision;
                m.SeenRemoved = EditorHost.RemovedCount(window.Code.ViewId);
            }
            // Grabbed panels and panels moved with Alt are brought forward
            window.SetZ(grab.Window == window || (keyMoved == window && keyLift > 0) ? -Grab.LiftDistance : 0);
            m.Step(WindowAngle(w, window), WindowY(w, window), window.Z, window.Scale, window == w.FocusWindow, dt);
            if (window == w.FocusWindow)
                FollowCaret(window, m, dt);
            else
                m.CaretShown = false;
            if (window.Code.Doc.Saved != m.SeenSaved)
            {
                m.SeenSaved = window.Code.Doc.Saved;
                m.Wave = 1;
                m.SavedAt = now;
                Cues.Play(Cues.Save, 0.35f);
            }
            if (window.Code.Doc.Landed != m.SeenLanded)
            {
                // When saved content gets into the game, light flies from the panel into the game space
                m.SeenLanded = window.Code.Doc.Landed;
                m.LandSeconds = now - m.SavedAt;
                m.LandLabel = 2.5f;
                Land(m, repeat, celebrate);
                celebrate = false;
                LastLandAt = now;
            }
            // When a line finishes sinking into the surface, a wave spreads from there
            var panel = Panel(window);
            if (panel.Step(dt) > 0)
            {
                m.Ripple = 1;
                m.RippleAt = panel.SankAt;
            }
            // When characters disappear from the text, particles scatter from where they were
            var removed = EditorHost.RemovedCount(window.Code.ViewId);
            if (removed != m.SeenRemoved)
            {
                m.SeenRemoved = removed;
                Cues.Play(Cues.Key, 0.1f, 0.68f + Cues.Random() * 0.1f);
                BurstAt(window, m, EditorHost.RemovedX(window.Code.ViewId), EditorHost.RemovedY(window.Code.ViewId),
                    Color.Rgb(0.95f, 0.75f, 0.35f, 1), 8, 0.35f);
            }
            if (window.Code.Doc.Revision != m.SeenRevision)
            {
                m.SeenRevision = window.Code.Doc.Revision;
                m.Pulse = 1;
            }
            if (w.Failures != m.SeenFailures)
            {
                // If the saved version has a compile error, the panel of the file with the error glows red and red particles spill
                m.SeenFailures = w.Failures;
                if (w.ErrorsIn(window.Code.Doc.Path).Count > 0)
                {
                    m.Alarm = 1;
                    m.Shake = Juice > 0 ? 1 : 0;
                    Cues.Play(Cues.Error, 0.7f);
                    particles.Burst(m.Angle.Value, m.Y.Value, Space.R - PanelView.FrontGap, 16, 0.35f, 0.5f,
                        Color.Rgb(1.0f, 0.3f, 0.25f, 1), 0.6f);
                }
            }
        }
        // When the error in a file without a panel is fixed, celebrate in the focused panel
        if (celebrate && w.FocusWindow != null && motions.TryGetValue(w.FocusWindow, out var here))
            Land(here, false, true);
        // A closed panel shrinks in place before disappearing
        var gone = new List<Window>();
        foreach (var pair in motions)
        {
            if (w.Windows.Contains(pair.Key))
                continue;
            var m = pair.Value;
            m.Closing = true;
            m.CaretShown = false;
            m.Step(m.Angle.Value, m.Y.Value, 0, WindowMotion.SmallScale, false, dt);
            if (m.Gone)
                gone.Add(pair.Key);
        }
        foreach (var g in gone)
            motions.Remove(g);
        if (w.FocusWindow != shownFocus)
        {
            if (shownFocus != null && w.FocusWindow != null && motions.TryGetValue(shownFocus, out var from)
                && motions.TryGetValue(w.FocusWindow, out var to))
            {
                cometFrom = from.Angle.Value;
                cometTo = to.Angle.Value;
                cometY = to.Y.Value + PanelView.BaseH * to.Scale.Value * 0.5f;
                comet = 0;
            }
            shownFocus = w.FocusWindow;
            Cues.Play(Cues.Move, 0.15f, 1.3f);
        }
        comet = Math.Min(1, comet + dt / 0.3f);
        keyLift -= dt;
        cold = Ease.Toward(cold, Paused ? 1 : 0, dt, 0.4f);
        particles.Step(dt);
        Flash = Math.Max(0, Flash - dt / 0.22f);
        for (var i = rings.Count - 1; i >= 0; i--)
        {
            rings[i].Age += dt;
            if (rings[i].Age >= Ring.Seconds)
                rings.RemoveAt(i);
        }
        for (var i = Landings.Count - 1; i >= 0; i--)
        {
            Landings[i].Age += dt;
            if (Landings[i].Age >= Landing.Seconds)
                Landings.RemoveAt(i);
        }
    }

    // Saved content reached the game: the panel frame shakes, the whole screen flashes, and rings and fragments fly.
    // Shortened while saving repeatedly. When an error is fixed, celebrate bigger than that
    static void Land(WindowMotion m, bool repeat, bool celebrate)
    {
        Cues.Play(celebrate ? Cues.Fixed : Cues.Land, celebrate ? 1 : repeat ? 0.45f : 0.8f);
        if (Juice == 0)
        {
            particles.Burst(m.Angle.Value, m.Y.Value, Space.R - PanelView.FrontGap, 8, 0.6f, -1.2f, Color.Rgb(0.4f, 0.95f, 1.0f, 1), 0.4f);
            return;
        }
        m.Shake = repeat && !celebrate ? 0.35f : 1;
        var r = Space.R - PanelView.FrontGap;
        particles.Burst(m.Angle.Value, m.Y.Value, r, repeat ? 10 : 28, 1.1f, -1.2f, Color.Rgb(0.4f, 0.95f, 1.0f, 1), 0.45f, 2.2f);
        if (!repeat || celebrate)
        {
            Flash = 1;
            rings.Add(new Ring { Angle = m.Angle.Value, Y = m.Y.Value, Size = PanelView.BaseH * m.Scale.Value });
        }
        if (!celebrate)
            return;
        // Confetti: the top reward, when what was shaking red passes
        var colors = new List<Color> { Color.Rgb(1, 0.82f, 0.2f), Color.Rgb(0.4f, 1, 0.55f), Color.Rgb(0.45f, 0.8f, 1), Color.Rgb(1, 0.45f, 0.7f), Color.Rgb(1, 1, 1) };
        foreach (var c in colors)
            particles.Burst(m.Angle.Value, m.Y.Value + PanelView.BaseH * m.Scale.Value * 0.3f, r, 16, 2.4f, -0.8f, c, 1.1f, 3);
        rings.Add(new Ring { Angle = m.Angle.Value, Y = m.Y.Value, Size = PanelView.BaseH * m.Scale.Value * 0.5f, Color = Color.Rgb(1, 0.82f, 0.2f) });
    }

    // Slides the cursor visual of the focused panel to where the editor last drew it (when focus arrives, shows it in place).
    // If the cursor advances on the same line and the line got longer by that much, the advanced part becomes a Landing as typed characters
    static void FollowCaret(Window window, WindowMotion m, float dt)
    {
        var id = window.Code.ViewId;
        if (EditorHost.CaretCount(id) == 0)
        {
            m.CaretShown = false;
            return;
        }
        var x = EditorHost.CaretX(id, 0);
        var y = EditorHost.CaretY(id, 0) + EditorHost.ScrollY(id);
        var line = EditorHost.CursorLine(id);
        var col = EditorHost.CursorCol(id);
        var length = Utf8.Bytes(EditorHost.Line(id, line));
        if (!m.CaretShown)
        {
            m.CaretX.Snap(x);
            m.CaretY.Snap(y);
            m.CaretShown = true;
        }
        else if (line == m.TypedLine && col > m.TypedCol && length - m.TypedLength == col - m.TypedCol && x > m.TypedX)
        {
            Landings.Add(new Landing(window, line, m.TypedX, x));
            Cues.Play(Cues.Key, 0.12f, 0.92f + Cues.Random() * 0.16f);
        }
        m.CaretX.Step(x, dt);
        m.CaretY.Step(y, dt);
        m.TypedLine = line;
        m.TypedCol = col;
        m.TypedLength = length;
        m.TypedX = x;
    }

    // Height on the cylinder of the line with the cursor
    static float CursorY(Window window, WindowMotion m)
    {
        var id = window.Code.ViewId;
        var panel = Panel(window);
        var v = (EditorHost.LineY(id, EditorHost.CursorLine(id)) + (text != null ? text.LineH * 0.5f : 0)) / window.PxH;
        return m.Y.Value + (0.5f - v) * window.UnscaledSize.Y * m.Scale.Value;
    }

    // Scatters particles from (x, y) px in the panel's view (top left of the line) (not if outside the panel)
    static void BurstAt(Window window, WindowMotion m, float x, float y, Color color, int n, float life)
    {
        var panel = Panel(window);
        var lh = EditorHost.LineHeight(window.Code.ViewId);
        if (y < 0 || y + lh > window.PxH || x < 0 || x > window.PxW)
            return;
        particles.Burst(panel.AngleAt(m.Angle.Value, m.Scale.Value, x), panel.YAt(m.Y.Value, m.Scale.Value, y + lh * 0.5f),
            Space.R - PanelView.FrontGap - 0.01f, n, 0.25f, -0.3f, color, life);
    }

    // Turns a point and direction in the XR LOCAL space into a ray in lider's space
    static Ray FromLocal(Vec3 pos, Vec3 dir)
    {
        var m = anchor.ToLocal();
        var values = new List<float>();
        for (var i = 0; i < 16; i++)
            values.Add(m[i]);
        var toLocal = Mat4.FromList(values, 0);
        var toAnchor = toLocal.Inverse();
        var p = toAnchor.MulPoint(pos);
        var d = toAnchor.MulDir(dir);
        return new Ray(new Vec3(p.X, p.Y, -p.Z), new Vec3(d.X, d.Y, -d.Z));
    }

    static void DrawEye(Workspace w, XrView view)
    {
        anchor.ViewProjection(view, eyeVp);
        // Screens fixed to the view are drawn in head coordinates
        viewVp = Space.FromXr(eyeVp) * headToWorld;
        // If a game placed in Space draws to the eyes, draw on top of it. If no game is placed there, clear the eyes first, then draw
        Gfx.BeginPass(new PassOpts { Target = view.Target, Load = w.InSpace != null ? Gfx.LoadAction.Load : Gfx.LoadAction.Clear, ClearColor = new float[] { 0, 0, 0, 1 } });
        DrawScene(w, Space.FromXr(eyeVp));
        Gfx.EndPass();
    }

    // A ring is 4 bars that get thinner as they spread
    static void DrawRing(Mat4 vp, Ring ring)
    {
        var t = ring.Age / Ring.Seconds;
        var size = ring.Size * (1 + 0.9f * t);
        var bar = 0.035f * (1 - t) * (1 - t) + 0.002f;
        var r = Space.R - PanelView.FrontGap - 0.03f;
        var da = size * 0.5f / r;
        Quad3d.CurvedSolid(vp, ring.Color, ring.Angle, ring.Y + size * 0.5f, r, size, bar);
        Quad3d.CurvedSolid(vp, ring.Color, ring.Angle, ring.Y - size * 0.5f, r, size, bar);
        Quad3d.CurvedSolid(vp, ring.Color, ring.Angle - da, ring.Y, r, bar, size);
        Quad3d.CurvedSolid(vp, ring.Color, ring.Angle + da, ring.Y, r, bar, size);
    }

    // Full-screen flash: mix in a dark color for an instant, then brighten, so it is visible on any background
    static void DrawFlash(Mat4 vp)
    {
        if (Flash <= 0)
            return;
        var at = Space.OnCylinderAt(lastEye != null ? gaze : PointerRay(viewportW * 0.5f, viewportH * 0.5f), Space.R);
        if (at == null)
            return;
        var color = Flash > 0.8f ? Color.Rgb(0, 0, 0, 0.35f) : Color.Rgb(1, 1, 1, 0.24f * Flash);
        Quad3d.CurvedSolid(vp, color, at.Angle, 0, Space.R - 0.35f, 12, 8);
    }

    // In XR, if the focused panel is outside the view, show a row of dots shrinking toward it beside where you are looking
    static void DrawFocusCue(Workspace w, Mat4 vp)
    {
        var window = w.FocusWindow;
        var at = Space.OnCylinderAt(gaze, Space.R);
        if (lastEye == null || window == null || at == null)
            return;
        var side = Space.OffView(at.Angle, WindowAngle(w, window), window.UnscaledSize.X * window.Scale * 0.5f / Space.R, CueFov);
        for (var i = 0; side != 0 && i < 3; i++)
        {
            var size = 0.05f - i * 0.015f;
            Quad3d.Solid(vp * Space.OnCylinder(at.Angle + side * (CueOffset + i * 0.05f), at.Y, Space.R - PanelView.FrontGap - 0.02f, size, size),
                Color.Rgb(0.5f, 0.9f, 1.0f, 0.9f));
        }
    }

    // Half-angle of the view outside which the focused panel counts as off view, and the angle from the look direction where the row of dots goes
    const float CueFov = 0.5f;
    const float CueOffset = 0.35f;

    static void DrawScene(Workspace w, Mat4 vp)
    {
        Quad3d.Opacity = Opacity;
        // While paused, wrap the space in a cold color
        if (cold > 0.001f)
            Quad3d.CurvedSolid(vp, Color.Rgb(0.25f, 0.45f, 1.0f, 0.07f * cold), 0, 0, Space.R + 2, 2 * (float)Math.PI * (Space.R + 2), 6);
        if (ZenShown)
            Quad3d.CurvedSolid(vp, Color.Rgb(0, 0, 0, 0.55f), 0, 0, Space.R + 2, 2 * (float)Math.PI * (Space.R + 2), 40);
        // Light that panels cast at their feet (the focused panel is a little brighter)
        foreach (var pair in motions)
        {
            // Only panels placed in the world cast light on the floor
            if (OnPlane(ScreenFor(w, pair.Key)))
                continue;
            var m = pair.Value;
            var a = m.Angle.Value;
            var at = Space.At(a, FloorY, FloorR);
            var size = pair.Key.UnscaledSize.X * m.Scale.Value * 1.4f;
            Quad3d.Soft(vp * Mat4.Translate(at) * Mat4.RotateY(Space.Facing(a)) * Mat4.RotateX(-(float)Math.PI / 2) * Mat4.Scale(new Vec3(size, size * 0.7f, 1)),
                Color.Rgb(0.35f, 0.75f, 1.0f, 0.06f + 0.06f * Math.Max(0, Math.Min(1, m.Glow.Value))));
        }
        var worldVp = vp;
        var worldFlat = Space.Flat;
        // In XR, images on flat screens (game image, inspector) are drawn as screen surfaces before everything else (outside XR, ScreenView puts them on the display)
        if (lastEye != null)
            foreach (var screen in Screens)
            {
                if (!screen.Shown || !OnPlane(screen))
                    continue;
                foreach (var window in screen.Windows)
                {
                    var whole = window.ViewportRect;
                    var part = window.ContentRect;
                    var picture = window.Content.Texture();
                    if (whole == null || part == null || picture == null || window.Position == null || whole.W <= 0)
                        continue;
                    // Convert the part of the rectangle on the display that shows the content into a rectangle on the screen surface
                    var perM = whole.W / window.Size.X;
                    var at = window.Position.Value;
                    var rect = new Vec4(at.X + (part.X + part.W * 0.5f - whole.X - whole.W * 0.5f) / perM,
                        at.Y - (part.Y + part.H * 0.5f - whole.Y - whole.H * 0.5f) / perM, part.W / perM, part.H / perM);
                    SurfaceQuad(screen, worldVp, rect, Color.Rgb(1, 1, 1, screen.Opacity), picture);
                }
            }
        // Titles of images on flat screens (game image, inspector) are drawn before everything else, like the images
        foreach (var screen in Screens)
            if (screen.Shown && OnPlane(screen))
                foreach (var window in screen.Windows)
                    if (!(window.Content is Code))
                        DrawTitle(w, window, worldVp);
        foreach (var pair in motions)
        {
            var window = pair.Key;
            var m = pair.Value;
            var angle = m.Angle.Value;
            var y = m.Y.Value;
            var scale = m.Scale.Value;
            var panel = Panel(window);
            // Panels on flat screens fixed to the view are drawn in view coordinates, without curving
            var onView = OnPlane(ScreenFor(w, window));
            vp = VpOf(ScreenFor(w, window), worldVp);
            Space.Flat = worldFlat || onView;
            panel.Flat = lastEye == null;
            panel.FrameAngle = (particles.Next() - 0.5f) * 0.03f * m.Shake * m.Shake;
            panel.FrameY = (particles.Next() - 0.5f) * 0.05f * m.Shake * m.Shake;
            panel.Draw(window.Code.Doc, angle, y, scale, vp, m.Z.Value, m.Glow.Value, m.Alarm);
            DrawTitle(w, window, worldVp);
            // The running-version board is a child of the code window: placed in the parent's coordinates and drawn in front of the parent
            var board = DiffOf(window);
            if (board != null && board.Position != null)
                panel.DrawOld(vp, angle + board.Position.Value.X * scale / window.Scale / Space.R, y + board.Position.Value.Y, board.Size.X * scale / window.Scale,
                    board.Size.Y * scale / window.Scale, Space.R + board.Position.Value.Z);
            foreach (var l in Landings)
                if (l.Window == window)
                    panel.DrawLanding(vp, window.Code.Doc, angle, y, scale, m.Z.Value, l);
            if (m.CaretShown)
            {
                var id = window.Code.ViewId;
                var scroll = EditorHost.ScrollY(id);
                PanelView.CaretDrawn = false;
                panel.DrawCaret(vp, window.Code.Doc, angle, y, scale, m.Z.Value, m.CaretX.Value, m.CaretY.Value - scroll);
                if (window == w.FocusWindow)
                    LogCaret(vp);
                // Cursors from the second one on (multiple selections) do not slide
                for (var i = 1; i < EditorHost.CaretCount(id); i++)
                    panel.DrawCaret(vp, window.Code.Doc, angle, y, scale, m.Z.Value, EditorHost.CaretX(id, i), EditorHost.CaretY(id, i));
            }
            var r = Space.R + m.Z.Value - PanelView.FrontGap - 0.005f;
            var width = window.UnscaledSize.X * scale * r / Space.R;
            // Save wave: a bright band flows from top to bottom
            if (m.Wave > 0)
                Quad3d.CurvedSolid(vp, Color.Rgb(0.6f, 0.95f, 1.0f, 0.45f * m.Wave), angle,
                    y + window.UnscaledSize.Y * scale * (m.Wave - 0.5f), r, width, 0.025f * scale);
            // Wave spreading up and down from the line that finished sinking
            if (m.Ripple > 0)
            {
                var spread = (1 - m.Ripple) * window.PxH * 0.6f;
                var face = Space.R + m.Z.Value - 0.003f;
                var band = 0.012f * scale;
                foreach (var py in new List<float> { m.RippleAt - spread, m.RippleAt + spread })
                    if (py >= 0 && py <= window.PxH)
                        Quad3d.CurvedSolid(vp, Color.Rgb(0.6f, 0.95f, 1.0f, 0.5f * m.Ripple), angle,
                            panel.YAt(y, scale, py) * face / Space.R, face, window.UnscaledSize.X * scale * face / Space.R, band);
            }
            // Red glow for compile errors
            if (m.Alarm > 0)
                Quad3d.CurvedSolid(vp, Color.Rgb(1.0f, 0.2f, 0.15f, 0.25f * m.Alarm), angle, y, r, width,
                    window.UnscaledSize.Y * scale);
            // Mark on the jumped-to line: a bright band between dark lines (visible on any background)
            if (m.Arrive > 0 && text != null && window == w.FocusWindow)
            {
                var cy = CursorY(window, m);
                var bandH = text.LineH / window.PxH * window.UnscaledSize.Y * scale;
                Quad3d.CurvedSolid(vp, Color.Rgb(0.5f, 0.9f, 1.0f, 0.5f * m.Arrive), angle, cy, r, width, bandH);
                Quad3d.CurvedSolid(vp, Color.Rgb(0, 0, 0, 0.7f * m.Arrive), angle, cy + bandH * 0.55f, r, width, bandH * 0.1f);
                Quad3d.CurvedSolid(vp, Color.Rgb(0, 0, 0, 0.7f * m.Arrive), angle, cy - bandH * 0.55f, r, width, bandH * 0.1f);
            }
            // The typed line flashes briefly
            if (m.Pulse > 0 && text != null && window == w.FocusWindow)
                Quad3d.CurvedSolid(vp, Color.Rgb(1.0f, 0.85f, 0.4f, 0.18f * m.Pulse), angle,
                    CursorY(window, m), r, width, text.LineH / window.PxH * window.UnscaledSize.Y * scale);
        }
        Space.Flat = worldFlat;
        // Game images on screens pasted on the display that are not flat are drawn on surfaces in space (images on flat screens pasted on the display are drawn by ScreenView)
        foreach (var screen in Screens)
        {
            if (!screen.Shown || OnPlane(screen))
                continue;
            foreach (var window in screen.Windows)
            {
                var picture = (window.Content is GameView) ? window.Content.Texture() : null;
                if (picture == null || window.Position == null)
                    continue;
                var a = WindowAngle(w, window);
                var y = WindowY(w, window);
                var on = VpOf(screen, worldVp);
                Theme.Plate(on, Theme.Surface, a, y, Space.R, window.Size.X, window.Size.Y);
                Quad3d.Curved(on, picture, Color.Rgb(1, 1, 1, 1), a, y, Space.R, window.Size.X, window.Size.Y, 0, 1);
                DrawTitle(w, window, worldVp);
            }
        }
        // Lights and marks attached to panels move together with the code screen
        vp = VpOf(w.CodeScreen, worldVp);
        // When focus moves, light flows from the previous panel to the next
        if (comet < 1)
        {
            var t = comet * comet * (3 - 2 * comet);
            var a = cometFrom + (cometTo - cometFrom) * t;
            Quad3d.Solid(vp * Space.OnCylinder(a, cometY, Space.R - PanelView.FrontGap - 0.02f, 0.04f, 0.04f),
                Color.Rgb(0.5f, 0.9f, 1.0f, 0.9f * (1 - comet * 0.5f)));
        }
        foreach (var p in particles.Live)
        {
            var size = (0.01f + 0.01f * p.Life / p.MaxLife) * p.Size;
            Quad3d.Solid(vp * Space.OnCylinder(p.Angle, p.Y, p.Radius, size, size),
                Color.Rgb(p.Color.R, p.Color.G, p.Color.B, p.Life / p.MaxLife));
        }
        // The inspector on a non-flat screen is drawn on a surface in space (images on flat screens are shown through the same path as the game image)
        if (TreeVisible && inspect.Texture != null && !OnPlane(ScreenFor(w, inspectWindow)))
        {
            var size = InspectSize();
            var on = VpOf(ScreenFor(w, inspectWindow), worldVp);
            Theme.Plate(on, Theme.Surface, BoardAngle, InspectY(), Space.R, size.X, size.Y);
            Quad3d.Curved(on, inspect.Texture, Color.Rgb(1, 1, 1, 1), BoardAngle, InspectY(), Space.R, size.X, size.Y, 0, 1);
            DrawTitle(w, inspectWindow, worldVp);
        }
        foreach (var ring in rings)
            DrawRing(vp, ring);
        DrawFlash(vp);
        DrawFocusCue(w, vp);
        DrawEdge(w, worldVp);
        // The on-screen keyboard is drawn in front of other windows
        var keys = w.ScreenOf(oskWindow);
        if (keys != null && keys.Shown && oskWindow.Position != null && osk.Picture != null)
            SurfaceQuad(keys, worldVp, new Vec4(oskWindow.Position.Value.X, oskWindow.Position.Value.Y, oskWindow.Size.X, oskWindow.Size.Y), Color.Rgb(1, 1, 1, 1), osk.Picture);
        DrawScreenTags(w, worldVp);
        // Points the controllers point at (both hands; shown on the pointed surface with the same apparent size). Drawn after the keyboard so they are visible over the on-screen keyboard too
        for (var h = 0; lastEye != null && h < 2; h++)
        {
            var on = aimScreen[h];
            if (on == null)
                continue;
            var dot = 0.014f / Apparent(on);
            SurfaceQuad(on, worldVp, new Vec4(aimAt[h].X, aimAt[h].Y, dot, dot), Color.Rgb(0.4f, 0.9f, 1.0f, 0.95f), null);
        }
        // Hints are drawn on a flat screen, without curving
        Space.Flat = true;
        hintCard.Draw(VpOf(hud, worldVp));
        Space.Flat = worldFlat;
        // Search and assets are also drawn on a flat screen, without curving
        Space.Flat = true;
        if (assetWindow.Position != null)
            AssetBoard.Draw(VpOf(palette, worldVp), WindowAngle(w, assetWindow), WindowTop(w, assetWindow), SearchScale);
        if (searchWindow.Position != null)
            searchView.Draw(VpOf(palette, worldVp), WindowAngle(w, searchWindow), WindowTop(w, searchWindow), SearchScale);
        Space.Flat = worldFlat;
        Quad3d.Opacity = 1;
    }
}
