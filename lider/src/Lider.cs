// lider の entry。
// `lub Lider.csproj` で起動し、LIDER_GAME のゲームを別の VM で開いて動かす (GameApp)。ゲームの絵を画面に写し、その上にコードの空間を描く。
// 配置 (どのスクリーンにどのウィンドウをどの規則で並べるか) は、名前で控えて戻せる (Arrangement)。
// 初めから、左にゲームの絵、右にコードの窓を並べる形と、コードの窓をゲームの絵に重ねる形が入っていて、起動したときは左右に並べる形になる。Alt+L で次の配置へ替える。
// キーボードは focus のあるウィンドウへ、マウスは指しているウィンドウへ送る。
// F1 で focus をゲームの絵と Code の間で移す (focus がゲームにある間、窓は同じ所に薄く残る)。押したウィンドウにも focus が移る。
// 起動したときは、ゲームの絵だけを出していて、focus はゲームにある (命令 screen:hide で、ゲームの絵だけにする / 戻す)。
// 操作は名前の付いた命令で、key の割り当ては Keymap にあり、Ctrl+P の検索からも名前で呼べる。
// key の割り当ては、命令 keys:set で変えられる (命令を選び、次に押した key を割り当てる)。変えた分は、利用者ごとの file に書く。命令 keys:reset で既定に戻す。
// 左 click は窓ならカーソルを置く。ゲームを止めている間は、何も無い所ならゲームの物を描いたコードを開き、中 click は、前にコードがあってもゲームの物を指す。
// 窓は横一列に並ぶ。非 XR の右ドラッグは、窓の上なら並びを変え (ドラッグ中のホイールで大きさを変える)、窓の外なら列を動かす。
// Alt を押している間は、スクリーンの輪郭と名前の札が出る。動かせるスクリーンは、札を右ドラッグで面に沿って動かし、ドラッグ中のホイールで手前と奥へ動かす (命令 screen:anchor で、視点に固定するか世界に置くかを替える)。
// Alt + 矢印で隣のウィンドウへ、Alt + Shift + 矢印で並びを変え、Alt + 数字でその番目の窓へ移る。非 XR では focus のある窓が見える所まで視点が滑る。
// env:
//   LIDER_GAME    開くゲーム。.csproj か、ディレクトリ (その下の .cs。web) なら lider が変換し (GameBuild)、.tcsb ならそのまま開く
//   LIDER_CS_LIB  LIDER_GAME がディレクトリのときに一緒に変換する lub の cs-lib のディレクトリ
//   LIDER_ENTRY   LIDER_GAME がディレクトリのときの entry class (無ければ OnFrame を持つ static class)
//   LIDER_ROOT    並べる .cs を探すディレクトリ (既定はゲームの .csproj のディレクトリ、LIDER_GAME が無ければ ".")
//   LIDER_SIZE    画面の大きさ ("幅x高さ")
//   LIDER_HOME    lider のディレクトリ (font を data/ から読む。既定 ".")
//   LIDER_ACTIVE  1 なら起動したときからスクリーンを全部出し、focus を Code に置く
//   LIDER_OPEN    起動したときに拡大する file の path
//   LIDER_REWIND  控える frame の数 (既定 600、0 で控えない)
// 時間: F5 でゲームを止める / 続ける、止めている間 F6 で 1 frame 戻り、F7 で 1 frame 進む。Shift+F5 でゲームを最初から作り直す。
// Ctrl+E で compile error の行へ順に飛ぶ。
// Alt+D で、保存した版と編集中の版 (保存していない本文で変換した版) のゲームを左右に並べる。もう一度 Alt+D で戻す。
// 並べている間、入力は両方のゲームに渡し、命令 game:copy-to-draft で保存した版の今の状態を編集中の版に写す (DraftGame)。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;
using LiderCore;
using LiderEditor;

public static class Lider
{
    public static Workspace? Work;
    // OnReload が呼ばれた回数 (reload chunk が当たった印)
    public static int Reloads;
    static MonoText? text;
    // 窓の本文の文字 (密度 → 文字)。text と桁も行の高さも同じ
    static readonly Dictionary<int, MonoText> panelTexts = new Dictionary<int, MonoText>();
    static readonly XrAnchor anchor = new XrAnchor();
    static readonly float[] eyeVp = new float[16];
    // 非 XR の視点の横の位置 (平面の x)。行き先と、行き先を決めたときの focus のある窓
    static readonly Spring viewX = new Spring(0);
    static float viewTarget;
    static Window? followed;
    // 非 XR で、インスペクタと focus のある窓を置く画面の範囲 (px、左上が 0,0。LIDER_AREA="x,y,w,h")。
    // AreaW が 0 なら決めない (窓は既定の大きさで、視点は focus のある窓を追う)
    public static float AreaX;
    public static float AreaY;
    public static float AreaW;
    public static float AreaH;
    // 範囲の幅のうち、インスペクタに使う割合
    const float AreaBoard = 0.34f;
    // 範囲を決めているときの、範囲の真ん中に合わせる高さ。窓の見出しも入れた高さの真ん中
    static float AreaCenterY => AreaW > 0 ? PanelView.TitleH * Space.FlatScale * 0.5f : 0;
    static float followedAngle;
    static float followedScale;
    static float followedBoard;
    static float followedOld;
    // 非 VR の縦の画角 (度)。
    // ホイールで狭めると拡大して読める
    static float fov = Space.FlatFov;
    static Mat4 lastVp = new Mat4();
    // lastVp を決めたか (lider の物を 1 度でも描いたか)
    static bool cameraSet;
    static int viewportW = 1;
    static int viewportH = 1;
    // 閉じる印を指している窓
    static Window? closeHover;
    static readonly Dictionary<string, PanelView> panels = new Dictionary<string, PanelView>();
    // VR のときの頭の向きと、Gfx.Pick に渡す左眼
    static Ray gaze = new Ray(new Vec3(0, 0, 0), new Vec3(0, 0, 1));
    static XrView? lastEye;
    // コントローラー: 右手の aim で指し、trigger で click、grip でつかむ (両手で広げる)、右の stick で scroll、止めている間は左の stick で frame を送る
    static readonly Grab grab = new Grab();
    // 手ごと (左 0、右 1) の、引き金を引いているか、指している面と、その面の上の位置
    static readonly bool[] triggerDown = new bool[2];
    static readonly List<Screen?> aimScreen = new List<Screen?> { null, null };
    static readonly List<Vec2> aimAt = new List<Vec2> { new Vec2(0, 0), new Vec2(0, 0) };
    // 押して引いている手 (押していなければ -1)。押せるのは、片方ずつ
    static int activeHand = -1;
    // 左ボタンか trigger を押した窓 (離すまで、指した所までを選ぶ)
    static Window? pressed;
    static bool rightGrip;
    static bool leftGrip;
    static float stepWait;
    static CylinderPoint? pointer;
    // 操作の手応え: 窓の見た目の動き、粒、focus が移ったときに流れる光
    static readonly Dictionary<Window, WindowMotion> motions = new Dictionary<Window, WindowMotion>();
    static readonly Particles particles = new Particles();
    static readonly List<Ring> rings = new List<Ring>();
    // 音と演出の強さ (命令 juice:cycle で 2 全部、1 音なし、0 演出も抑える)
    public static int Juice = 2;
    // 全面の明滅 (1 から 0 へ減る。最初は暗く、次に明るく)
    public static float Flash;
    public static float LastLandAt = -100;
    static int seenFixed;
    static int seenJumps;
    // 続けて保存している間は、演出を短くする秒
    const float RepeatSeconds = 2;
    static Window? shownFocus;
    static float cometFrom;
    static float cometTo;
    static float cometY;
    static float comet = 1;
    // Alt で並びか大きさを変えた窓と、まだ浮かせておく秒
    static Window? keyMoved;
    static float keyLift;
    // 止めている間の空間の冷え (0 から 1) と、控えた frame の帯の上の点の位置
    static float cold;
    public static float Cold => cold;
    static readonly Spring timeline = Stiff();
    static bool timelineShown;
    // 打った文字が面に収まる動き
    public static readonly List<Landing> Landings = new List<Landing>();
    public static int ParticleCount => particles.Live.Count;
    // ゲームの物を指して Gfx.Pick の結果を待っている間
    static bool pickPending;
    static float pickX;
    static float pickY;
    // 調べているのが、click でなく指しているだけの物か
    static bool hoverPick;
    static float hoverX;
    static float hoverY;
    static float hoverStill;
    // 指しているゲームの物を描いたコード (内側から)
    public static List<string> What = new List<string>();
    // 画面の座標 (web は CSS px) の 1 に対する、MainTex の画素の数
    public static float PixelRatio = float.Parse(Environment.GetEnvironmentVariable("LIDER_PIXEL_RATIO") ?? "1");
    // インスペクタ (Alt+I で出して focus を移し、focus があればしまう。左手の X でも出し入れ)。中身が Inspector のウィンドウで、コードの窓と同じスクリーンに並ぶ。
    // 重ねる形ではコードのスクリーンの一番左、左右に並ぶ形では背景のスクリーンの最後に置く
    static readonly InspectView inspect = new InspectView();
    public static InspectView Inspector => inspect;
    static Window inspectWindow = new Window(new Inspector(inspect));
    public static Window InspectWindow => inspectWindow;
    static readonly TitleView inspectTitle = new TitleView("lider_inspect_title");
    static bool TreeVisible => inspect.Shown && !ZenShown && inspectWindow.Position != null;
    // 窓を画面の幅に広げ、左の板を隠し、ゲームを暗くする。focus がゲームにある間は効かせない (ゲームを遊べるように)
    public static bool Zen;
    static bool ZenShown => Zen && Work != null && !Work.OnGame;
    // 空間に置いたスクリーン (奥のゲームの絵と、コードの窓の円筒)。
    // 視点に固定した平面のスクリーンは ScreenView が先に描き、コードの窓と板は、その手前に DrawScene が描く
    public static readonly List<Screen> Screens = new List<Screen>();
    // 画面のキーボード (命令 keyboard:toggle と、左手の Y で出し入れする)。中身が Osk のウィンドウで、最初は正面の下の手前のスクリーン (desk) に出す
    static readonly Osk osk = new Osk();
    public static Osk Keyboard => osk;
    static readonly OskView oskView = new OskView();
    static Window oskWindow = new Window(osk);
    public static Window KeyboardWindow => oskWindow;
    static Screen desk = new Screen("desk");
    // 画面のキーボードの大きさの倍率 (1 で、窓の本文と同じ画素の大きさ)
    const float OskScale = 0.75f;
    static bool oskButton;
    // 一番奥のスクリーン: 視野をちょうど覆い、ゲームの絵を映す
    static Screen backdrop = new Screen("backdrop");
    // 保存した版のゲームを映すウィンドウ
    static Window gameWindow = new Window(new GameView());
    // 編集中の版のゲームを映すウィンドウ (並べている間だけ)
    static Window? draftWindow;
    const float BackdropDistance = 10;
    // lider の描く物の濃さ (focus がゲームにある間は GameOpacity へ寄せる)
    public static float Opacity = 1;
    const float GameOpacity = 0.35f;
    static bool inspectButton;
    // 手前に出す板 (動いている版の板、範囲を決めたときの帯) の半径
    const float InspectR = Space.R - 0.12f;
    // 非 XR は窓を大きく開くので、インスペクタの字も同じくらいにする
    static float InspectScale => Space.Flat ? 1.4f : 1;
    // 指している物に対して、そのとき効く操作の案内
    static readonly HintCard hintCard = new HintCard();
    // 案内のウィンドウと、それを置くスクリーン (視点に固定した平面。右下の隅に積む)
    static Window hintWindow = new Window(new Hints(hintCard));
    static Screen hud = new Screen("hud");
    public static Window HintWindow => hintWindow;
    // file と宣言を引く検索 (Ctrl+P)。板を置く円筒の角度と上の端の高さ
    public static readonly Search Search = new Search();
    static readonly SearchView searchView = new SearchView();
    public static readonly AssetView AssetBoard = new AssetView();
    // フィールドの宣言の行に出す今の値
    public static readonly FieldValues Fields = new FieldValues();
    // 行ごとの負荷 (窓の行の列か札と、インスペクタの板の負荷の一覧に出す)
    public static readonly LoadProfile Costs = new LoadProfile();

    static int fieldFrames;
    // 宣言を読み直す間隔
    const int ScanFrames = 15;
    // 検索とアセットのウィンドウと、それを置くスクリーン (視点に固定した平面。上の端の真ん中から下へ積む)
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
        // 本文と動いている版の違いを、最初から窓の中 (インライン) に出す (命令 code:diff-side で切り替えられる)
        if (Environment.GetEnvironmentVariable("LIDER_DIFF") == "inline")
            PanelView.SideBySide = false;
        // 止める前の frame へ戻れるよう、lider を出していなくても起動したときから控える。
        // 控えている間、ゲームを進める前ごとにゲームの状態 (static と、そこから辿れる object) を全部写すので、その分だけ毎 frame 重い。
        // LIDER_REWIND=0 で控えない
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
        // 画面のキーボードを置く、正面の下の手前の面 (世界に置く)
        desk = new Screen("desk") { Anchor = ScreenAnchor.World, Position = new Vec3(0, -0.3f, 1.1f), Size = new Vec2(1.4f, 0.42f), Layout = new Stack { AlignX = 0 } };
        oskWindow = new Window(osk);
        osk.Shown = false;
        osk.Mods = 0;
        Screens.Add(desk);
        // スクリーンの一覧は 1 つだけ持つ
        Work.Screens = Screens;
        backdrop.Layout = new Even();
        // 起動したときは、ゲームだけを出し、focus もゲームに置く (LIDER_ACTIVE=1 なら、最初から全部出して focus を Code に置く)
        gameHeld = false;
        cameraSet = false;
        SetHidden(true);
        if (Environment.GetEnvironmentVariable("LIDER_ACTIVE") == "1")
            FocusCode();
        RegisterCommands();
        BuiltinArrangements(Work);
        // 起動したときは、左右に並ぶ形にする
        Arrange(SideBySide);
        Work.Drafts?.Restore(Work);
        // lider の VM を作り直した (lider 自身の static の初期化子の変更) なら、作り直す前の窓に戻す
        if (Session.Restore(Work, Sys.Seconds()))
            return;
        var open = Environment.GetEnvironmentVariable("LIDER_OPEN");
        if (open != null)
            Work.FocusOn(open);
    }

    // lub が VM を作り直す前と、終わる前に呼ぶ
    public static void OnQuit()
    {
        if (Work != null)
            Session.Save(Work, Sys.Seconds());
    }

    // ゲーム (LIDER_GAME) のディレクトリ (LIDER_GAME が無ければ ".")
    static string GameDir()
    {
        var module = GameApp.Project;
        for (var i = Utf8.Bytes(module) - 1; i > 0; i--)
            if (Utf8.At(module, i) == '/')
                return Utf8.Sub(module, 0, i);
        return ".";
    }

    // 保存した版がゲームに入った (reload が当たったか、作り直した)
    public static void GameReloaded()
    {
        Reloads++;
        Work?.Reloaded();
    }

    // 当てた差分に、動いている物には入らない変更があった (GameReloaded の後に呼ぶ)
    public static void GameReloadNotes(string notes)
    {
        Work?.ReloadNotes(notes);
    }

    // 窓の file で宣言した field の、動いている個体に入らなかった初期値の変更の知らせ。
    // 宣言が見つからない field は focus のある窓に出す
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
        return notices;
    }

    // 保存した版が compile error で、ゲームに入らなかった
    public static void GameBuildFailed(string errors)
    {
        Work?.BuildFailed(errors);
    }

    // 控える frame の数 (LIDER_REWIND の値。無いか読めなければ 600、0 で控えない) を決める
    public static void Record(string? frames)
    {
        GameApp.Keep(frames != null && int.TryParse(frames, out var n) ? n : 600);
    }

    // focus をゲームの絵のウィンドウに移す (キーボードがゲームに届く)
    public static void FocusGame()
    {
        if (Work == null)
            return;
        Work.FocusTo(gameWindow);
        if (Search.Shown)
            Search.Cancel(Work);
    }

    // focus を Code のウィンドウに移す (隠していたスクリーンは出す)
    public static void FocusCode()
    {
        if (Work == null)
            return;
        SetHidden(false);
        Work.FocusCode();
    }

    // ゲームの絵だけを出しているか
    public static bool Hidden => Work != null && hidden;
    static bool hidden;

    // ゲームの絵だけを出す / 戻す。ゲームの絵だけにするときは、focus をゲームに移す
    public static void SetHidden(bool hidden)
    {
        if (Work == null)
            return;
        Lider.hidden = hidden;
        PutAway();
        if (hidden)
            FocusGame();
    }

    // 隠している間は、ゲームの絵だけを出す: ゲームの絵の無いスクリーンは出さず、ゲームの絵のあるスクリーンでは、ほかのウィンドウをしまう。
    // 隠している間に足されたウィンドウにも効くよう、毎 frame 決め直す
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

    // lider の描く物 (ゲームの絵でないウィンドウ、検索、案内) を出しているか
    static bool LiderShown(Workspace w)
    {
        return !hidden;
    }

    // ゲームの絵の上でマウスのボタンを押し、まだ離していないか (離すまで、マウスの入力はゲームに送る)
    static bool gameHeld;

    // 画面の (x, y) のマウスの入力がゲームのものか。
    // ゲームが動いている間、lider の物を指していなければゲームに渡す。止めている間は、ゲームの絵の上も lider が受ける (描いたコードを調べる)
    static bool MouseForGame(Workspace w, float x, float y)
    {
        // ゲームの絵のウィンドウの見出しの帯は、lider の物
        if (lastEye == null && !gameHeld && GameTitleAt(x, y) != null)
            return false;
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

    // ray が lider の物 (ウィンドウ、その見出しと端、検索の板) を指しているか
    static bool OverLider(Workspace w, Ray ray)
    {
        return Search.Shown || OskUv(w, ray) != null || TagAt(w, ray) != null || OnSpaceTag(ray) || InspectAt(ray) != null || InspectTitleAt(ray) != null || TitleAt(w, ray) != null || WindowAt(w, ray) != null
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
        // キーボードは focus のあるウィンドウへ、マウスは指しているウィンドウへ、そのほか (gamepad など) はゲームへ送る
        if (!key && !mouse)
        {
            SendGame(e);
            return;
        }
        if (key && w.OnGame)
        {
            // focus がゲームにある間、lider が取る key は、focus を戻す key だけ
            var name = Document.Booted ? EditorHost.NameOf(e.Key, e.Plain) : null;
            if (name != null && Keymap.Binds(name, "focus:game"))
            {
                if (e.Kind == EventKind.KeyDown && !repeated)
                    CommandTable.Run("focus:game");
                return;
            }
            // エディタを起こす前は、表を引けないので F1 で戻す
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
            // 押し始めの行き先は、指している所で決める
            if (e.Kind == EventKind.MouseButtonDown)
                gameHeld = false;
            var forGame = MouseForGame(w, x, y);
            if (e.Kind == EventKind.MouseButtonDown)
                gameHeld = forGame;
            if (e.Kind == EventKind.MouseButtonUp)
                gameHeld = false;
            if (forGame)
            {
                // ゲームの絵を押すと、focus もゲームに移る
                if (e.Kind == EventKind.MouseButtonDown && !w.OnGame)
                    FocusGame();
                SendGame(e);
                return;
            }
        }
        if (Search.Shown && SearchEvent(w, e))
            return;
        // key は、表 (Keymap) を通って命令になる。命令にならない key は、focus のある Code に入る
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
            // スクリーンの名前の札の上なら、スクリーンをつかむ。ほかは、指しているウィンドウをつかむ
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
            // 窓の上なら scroll、Ctrl を押していれば画角を変えて拡大する
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

    // 検索を出している間の入力 (修飾 key の押し離しは Workspace にも渡す)。扱ったら true
    static bool SearchEvent(Workspace w, EventData e)
    {
        if (e.Kind == EventKind.MouseButtonDown)
        {
            // 板の上を押しても、下の窓には届かない
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
        // key を待っている間は、次に押した key を、選んだ命令に割り当てる
        if (keyFor != null)
        {
            TakeKey(w, e);
            return true;
        }
        // focus をゲームへ移す key は、検索を出していても効く (検索は閉じる)
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

    // 検索とアセットのウィンドウを、出している間だけスクリーンに載せ、板の大きさにして、規則 (上の端の真ん中から下へ) で置く
    static void PlacePalette(Workspace w)
    {
        Show(palette, searchWindow, Search.Shown, 0);
        Show(palette, assetWindow, w.ShownAsset != "", palette.Windows.Count);
        searchWindow.Size = new Vec2(SearchView.W * SearchScale / ViewScale(palette), searchView.Height(SearchScale) / ViewScale(palette));
        assetWindow.Size = new Vec2(AssetView.W * SearchScale / ViewScale(palette), AssetView.H * SearchScale / ViewScale(palette));
        palette.Arrange();
    }

    // window を、shown の間だけ screen の並びの at 番目に載せる
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

    // ウィンドウの上の端の高さ (板は、上の端の真ん中を決めて描く)
    static float WindowTop(Workspace w, Window window)
    {
        return WindowY(w, window) + window.Size.Y * ViewScale(ScreenFor(w, window)) * 0.5f;
    }

    // スクリーンの輪郭と名前の札: Alt を押している間と、スクリーンをつかんでいる間だけ出す。
    // 動かせるスクリーンは、札を右ボタンでつかむと面に沿って動き、つかんだままホイールで手前と奥へ動く
    static readonly Dictionary<Screen, TitleView> screenTags = new Dictionary<Screen, TitleView>();
    static Screen? heldScreen;
    // つかんだ点 (スクリーンの面の上の座標)。動かしている間、指す点がここに重なるようにスクリーンを運ぶ
    static Vec2 heldAt;
    // 札の文字の大きさ (1 で、窓の本文と同じ)、輪郭の線の太さと、輪郭の外に取る余白 (半径 Space.R の所のメートル)
    const float TagScale = Space.FlatScale;
    const float OutlineW = 0.004f;
    const float OutlinePad = 0.02f;

    public static bool ScreensLit => Work != null && (heldScreen != null || (Work.Alt && !Work.OnGame && LiderShown(Work)));
    public static Screen? HeldScreen => heldScreen;

    // 面の上の長さを、半径 Space.R の所の同じ見かけの長さにする倍率
    static float Apparent(Screen screen)
    {
        return OnPlane(screen) ? ViewScale(screen) : 1;
    }

    // screen の輪郭 (スクリーンの面の上の矩形: 真ん中の x と y、幅、高さ)。
    // 大きさを持つスクリーンはその大きさ、並べた物で決まるスクリーンは、出ているウィンドウを囲む矩形
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
            // コードの窓は、本文の上に見出しが乗る
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

    // screen の名前の札の矩形 (スクリーンの面の上の、真ん中の x と y、幅、高さ)。札を出していなければ null。
    // 輪郭の左上の内側に置く。大きさを持つスクリーンは輪郭が重なるので、スクリーンの順に横へ並べる。
    // ほかのスクリーンは、輪郭が画面からはみ出していても札をつかめるよう、輪郭のうち画面に見えている所の左上に置く
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
                // 画面の上の端の 1 段は、大きさを持つスクリーンの札が並ぶので空ける
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

    // Space の札: Space にゲームを置いている間、スクリーンの名前の札の列の右に出す。押すと、ゲームが元のスクリーンのウィンドウに戻る。
    // 札は、奥のゲームの絵のスクリーン (backdrop) の面の上に置く
    static readonly TitleView spaceTag = new TitleView("lider_space_tag") { Mark = false };

    // Space の札の矩形 (backdrop の面の上の、真ん中の x と y、幅、高さ)。出していなければ null
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

    // ray が Space の札を指しているか
    static bool OnSpaceTag(Ray ray)
    {
        var tag = SpaceTagOf();
        var at = tag != null ? SurfacePoint(backdrop, ray) : null;
        return tag != null && at != null && Math.Abs(at.Value.X - tag.Value.X) <= tag.Value.Z * 0.5f && Math.Abs(at.Value.Y - tag.Value.Y) <= tag.Value.W * 0.5f;
    }

    // ray が指している名前の札のスクリーン (手前のスクリーンを先に見る)
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

    // 画面の (x, y) が、動かせるスクリーンの名前の札の上なら、そのスクリーンをつかむ
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

    // つかんでいるスクリーンを、つかんだ点が ray の指す所に来るように、面に沿って動かす
    static void DragScreen(Ray ray)
    {
        var at = heldScreen != null ? SurfacePoint(heldScreen, ray) : null;
        if (heldScreen != null && at != null)
            heldScreen.Shift(at.Value.X - heldAt.X, at.Value.Y - heldAt.Y);
    }

    // 札の texture を、出している間だけ描く (画面への pass の外で呼ぶ)
    static void PrepareScreenTags(Workspace w, MonoText text)
    {
        if (!ScreensLit)
            return;
        foreach (var screen in Screens)
        {
            if (!screenTags.TryGetValue(screen, out var tag))
            {
                tag = new TitleView("lider_screen_tag_" + screen.Name) { Mark = false };
                screenTags[screen] = tag;
            }
            tag.Label = screen.Name;
            // 動かせないスクリーンの札は、薄く出す
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

    // screen の面の上の (x, y) の、最後に描いた frame の画面の px
    public static Vec2 SurfacePx(Screen screen, float x, float y)
    {
        var flat = Space.Flat;
        Space.Flat = flat || screen.Shape == ScreenShape.Plane;
        var k = Apparent(screen);
        var p = OnPlane(screen) ? Space.At((screen.Position.X + x) / screen.Position.Z, (screen.Position.Y + y) * k, Space.R) : Space.At(screen.AngleAt(x), y, Space.R);
        Space.Flat = flat;
        return ViewportOn(screen, p);
    }

    // screen の面の上の矩形 (真ん中の x と y、幅、高さ) を、色か texture で描く
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
            // 輪郭は、矩形の内側に引く (画面いっぱいのスクリーンでも見える)
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

    // 指しているウィンドウをマウスでつかむ (どの中身のウィンドウでも、どのスクリーンの上でも)。
    // ゲームの絵は、見出しの帯でつかむ (絵の上のマウスはゲームのもの)
    static void GrabAt(Workspace w, float x, float y)
    {
        var ray = PointerRay(x, y);
        var window = GameTitleAt(x, y);
        if (window == null && (InspectTitleAt(ray) != null || InspectAt(ray) != null))
            window = inspectWindow;
        window ??= (TitleAt(w, ray) ?? WindowAt(w, ray))?.Window;
        if (window == null || window.Position == null)
            return;
        var at = SurfacePoint(ScreenFor(w, window), ray);
        if (at == null)
            return;
        grab.Start(window, at.Value.X, at.Value.Y);
        // 次に並べるときから、配置の規則はこのウィンドウを動かさない
        ScreenFor(w, window).Held = window;
    }

    // ray が screen の面に当たる所 (スクリーンの面の上の座標。当たらなければ null)
    static Vec2? SurfacePoint(Screen screen, Ray ray)
    {
        var flat = Space.Flat;
        Space.Flat = flat || screen.Shape == ScreenShape.Plane;
        var at = Space.OnCylinderAt(RayOn(screen, ray), Space.R);
        Space.Flat = flat;
        if (at == null)
            return null;
        // 視点に固定した平面は、半径 Space.R の所に同じ見かけの大きさで当てているので、スクリーンの距離の大きさに戻す
        if (OnPlane(screen))
            return new Vec2(at.Angle * screen.Position.Z - screen.Position.X, at.Y / ViewScale(screen) - screen.Position.Y);
        return new Vec2(screen.XAt(at.Angle), at.Y);
    }

    // ウィンドウを閉じる (どの中身のウィンドウにも効く)。focus があったら、Code のウィンドウに戻す
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

    // focus のあるウィンドウを閉じる (Code では今のタブを閉じ、最後のタブならウィンドウごと閉じる)
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

    // インスペクタの行に絞り込む。個体なら、エディタのフィールドの値もその個体にする
    public static void EnterScope(InspectRow row)
    {
        inspect.Tree.Enter(row);
        if (row.Id != "")
            Fields.Select(row.Key);
    }

    // 絞り込んだ個体を、static から辿って見つけた所 (FieldValues) に追わせる。見つからなくなったら class に戻す
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

    // 実行の印を引いている個体 (絞り込みの ScopeId。替わったら印を消す)
    static string tracedScope = "";

    // id の個体に絞る (static から辿って見つけていなければ何もしない)
    public static void ScopeToInstance(string id)
    {
        var found = Fields.Find(id);
        if (found == null)
            return;
        Fields.Select(found.Path);
        inspect.Tree.EnterInstance(found.Class, id, found.Path);
    }

    // インスペクタをパンくずの i 番目の段に戻す。個体から抜けたら、エディタの個体の選択も解く
    public static void ScopeTo(int i)
    {
        var cls = inspect.Tree.ScopeClass;
        var id = inspect.Tree.ScopeId;
        inspect.Tree.ScopeTo(i);
        if (id != "" && inspect.Tree.ScopeId == "" && Fields.IsSelected(cls, id))
            Fields.Toggle(cls, id);
    }

    // エディタの class の行の個体の目印を押した: その個体を選び、インスペクタもその個体に絞り込む (選んでいれば解き、class に戻す)
    public static void ToggleInstance(string cls, string id)
    {
        Fields.Toggle(cls, id);
        var path = Fields.PathOf(cls, id);
        if (Fields.IsSelected(cls, id) && path != "")
            inspect.Tree.EnterInstance(cls, id, path);
        else if (inspect.Tree.ScopeId == id)
            inspect.Tree.ScopeTo(1);
    }

    // 窓の i 番目のタブを出して focus を移す (見出しのタブを押した)
    public static void ShowTab(Window window, int i)
    {
        var w = Work;
        if (w == null || i < 0 || i >= window.Code.Tabs.Count)
            return;
        window.Code.Tab = i;
        w.FocusWindow = window;
    }

    // Alt+Shift+PageUp / PageDown で変える幅 (px、約 8 桁)
    const int WidthStep = 64;

    // lider の操作を、名前の付いた命令として表に載せる (key の割り当ては Keymap、検索からも同じ名前で呼ぶ)
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
        CommandTable.Register("window:widen", "ウィンドウの幅を広げる", () => Moved(w => w.FocusWindow!.Resize(w.FocusWindow.WidthPx + WidthStep, w.FocusWindow.HeightPx))).Repeats = true;
        CommandTable.Register("window:narrow", "ウィンドウの幅を狭める", () => Moved(w => w.FocusWindow!.Resize(w.FocusWindow.WidthPx - WidthStep, w.FocusWindow.HeightPx))).Repeats = true;
        CommandTable.Register("window:zen", "ウィンドウを画面いっぱいに広げる・戻す", () => With(w => Zen = !Zen));
        CommandTable.Register("window:move-to-next-screen", "ウィンドウを次のスクリーンへ移す", () => With(w =>
        {
            if (w.Focused != null)
                MoveToNextScreen(w, w.Focused);
        }));
        CommandTable.Register("arrange:next", "次の配置へ", () => With(w => NextArrangement()));
        CommandTable.Register("arrange:save", "今の配置を、名前を付けて控える", () => With(w => Search.Ask(w, "配置の名前", SaveArrangement)));
        CommandTable.Register("inspector:toggle", "インスペクタを開く・focus を移す・閉じる", () => With(w =>
        {
            // 出していなければ出して focus を移し、focus がほかにあれば focus を移し、focus があれば閉じる
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

    // 命令を名前で実行する (検索と test から)
    public static bool Run(string name)
    {
        return CommandTable.Run(name);
    }

    // どの focus でも効く命令
    static bool With(Action<Workspace> run)
    {
        if (Work == null)
            return false;
        run(Work);
        return true;
    }

    // focus が Code にあるときだけ効く命令
    static bool OnCode(Action<Workspace> run)
    {
        var w = Work;
        if (w == null || w.FocusWindow == null || w.Focused != w.FocusWindow)
            return false;
        run(w);
        return true;
    }

    // focus のある Code のウィンドウを動かす命令。動かしている間は、つかんだときと同じに浮かせる (key の繰り返しが続く間)
    static bool Moved(Action<Workspace> run)
    {
        if (!OnCode(run))
            return false;
        keyMoved = Work!.FocusWindow;
        keyLift = 0.3f;
        return true;
    }

    // focus のあるウィンドウから (dx, dy) の向きにある、同じスクリーンの一番近いウィンドウへ focus を移す (+X が右、+Y が上)
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
        // 同じスクリーンの中で探す (重なっている別のスクリーンのウィンドウとは、隣かどうかを決められない)
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
                // 向きの先にあり、横へのずれより先への進みが大きい物
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

    // ウィンドウの真ん中の、見た目の位置 (半径 Space.R の所のメートル。視点の正面が 0)。
    // 視点に固定した平面のスクリーンと、世界に置いた円筒のスクリーンのウィンドウを、同じ物差しで比べる
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

    // 一番新しい frame から 1 frame 進めている間、止め直すまでに残る frame の数
    static int stepping;
    // 止めているか (1 frame 進めている間も止めているとみなす)
    static bool Paused => GameApp.Position >= 0 || stepping > 0;

    // ゲームを止める (止めていれば、止めた frame から続ける)
    public static void TogglePause()
    {
        if (GameApp.Position < 0)
            GameApp.Pause(0);
        else
            GameApp.Resume();
        stepping = 0;
    }

    // 止めている間、1 frame 前へ
    public static void StepBack()
    {
        var at = GameApp.Position;
        if (at >= 0 && at + 1 < GameApp.Count)
            GameApp.Pause(at + 1);
    }

    // 止めている間、1 frame 後へ。
    // 一番新しい frame なら、ゲームを 1 frame 動かして控えに足し、そこで止める。
    // 続けると止めた frame を今の入力で作り直すので、2 frame 動かして次の frame で止め直す (進めている間に押すと 1 frame 足す)
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

    // LIDER_CARET_LOG=1 なら、focus のある窓の cursor の画面の位置 (px、左上が 0,0) と高さを、変わったときに標準出力へ
    // 「lider_caret x y h」と書く (録画の編集が、打っている所を追うため)
    static readonly bool caretLog = Environment.GetEnvironmentVariable("LIDER_CARET_LOG") == "1";
    static string loggedCaret = "";
    // focus のある窓の cursor の上の端の画面の位置 (px) と高さ (Z)。描いていない frame は前のまま
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

    // LIDER_CARET_LOG=1 なら、打った key が入る file (focus のある窓の今のタブ。lider が入力を取っていないか、
    // ファイルの検索を出しているか、窓が無ければ空) を、変わったときに標準出力へ「lider_focus PATH」と書く
    // (録画の道具が、打ち込む前に打つ先の file を確かめるため)
    static string? loggedFocus;

    static void LogFocus(Workspace w)
    {
        var focus = !w.OnGame && !Search.Shown && w.Focus != null ? w.Focus.Path : "";
        if (!caretLog || focus == loggedFocus)
            return;
        loggedFocus = focus;
        Console.WriteLine("lider_focus " + focus);
    }

    // インスペクタの板の、最後に描いた frame の画面の px (左, 上, 右, 下)
    public static Vec4 BoardViewport()
    {
        var size = InspectSize();
        var a = ViewportPoint(Space.At(BoardAngle - size.X * 0.5f / Space.R, InspectY() + size.Y * 0.5f, Space.R));
        var b = ViewportPoint(Space.At(BoardAngle + size.X * 0.5f / Space.R, InspectY() - size.Y * 0.5f, Space.R));
        return new Vec4(a.X, a.Y, b.X, b.Y);
    }

    // 控えた frame の帯 (止めた位置の印の高さを含む) の、画面の px (左, 上, 右, 下)。出していなければ全部 0
    public static Vec4 TimelineViewport()
    {
        var band = TimelineRect();
        return band != null ? new Vec4(band.X, band.Y, band.X + band.W, band.Y + band.H) : new Vec4(0, 0, 0, 0);
    }

    // コードのスクリーンの上の位置 (基準の置き方の座標) の、最後に描いた frame の画面の px (左上が 0,0)
    public static Vec2 ViewportPoint(Vec3 p)
    {
        return Work != null ? ViewportOn(Work.CodeScreen, p) : ViewportOf(lastVp, p);
    }

    // screen の上の位置 (基準の置き方の座標) の、最後に描いた frame の画面の px
    public static Vec2 ViewportOn(Screen screen, Vec3 p)
    {
        return ViewportOf(VpOf(screen, lastVp), p);
    }

    // 空間の位置の、画面の px (左上が 0,0)
    static Vec2 ViewportOf(Mat4 vp, Vec3 p)
    {
        var c = vp * new Vec4(p.X, p.Y, p.Z, 1);
        return new Vec2((c.X / c.W * 0.5f + 0.5f) * viewportW, (0.5f - c.Y / c.W * 0.5f) * viewportH);
    }

    // 画面の (x, y) (VR では頭の向き) を click する
    public static void ClickAt(float x, float y)
    {
        var w = Work;
        if (w == null)
            return;
        // ゲームの絵のウィンドウの見出しを押すと focus が移り、閉じる印なら閉じる
        var bar = lastEye == null ? GameTitleAt(x, y) : null;
        if (bar != null)
        {
            if (OnGameClose(bar, x))
                Close(w, bar);
            else if (OnGameSpace(bar, x))
                w.PutInSpace(bar);
            else
                w.FocusTo(bar);
            return;
        }
        // Space の札を押すと、Space に置いたゲームが、元のスクリーンのウィンドウに戻る
        if (OnSpaceTag(lastEye != null ? gaze : PointerRay(x, y)))
        {
            w.TakeFromSpace();
            return;
        }
        pickX = x;
        pickY = y;
        Click(w, lastEye != null ? gaze : PointerRay(x, y), true);
    }

    // コードの板が前にあっても、その奥のゲームの物を描いたコードを探す (中ボタン)。ゲームを止めている間だけ効く
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

    // 指した所で次のどれかをする: 窓の閉じる印なら閉じる、窓の見出しならその窓に focus を移す、窓ならその窓に focus を移してカーソルを動かす、どれでもなければゲームの物を描いたコードを探す (Gfx.Pick)
    public static void Click(Workspace w, Ray ray, bool pickGame)
    {
        if (PressOsk(w, ray))
            return;
        var bar = InspectTitleAt(ray);
        if (bar != null)
        {
            if (inspectTitle.OnClose(bar.Value.X))
                Close(w, inspectWindow);
            else
                w.FocusTo(inspectWindow);
            return;
        }
        var uv = InspectAt(ray);
        // 押したウィンドウに focus が移る (コードへ飛ぶ行を押したら、focus は開いた Code に移る)
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
            return;
        }
        var edged = !Zen && lastEye == null ? EdgeWindow(w, ray) : null;
        if (edged != null)
        {
            w.FocusTo(edged);
            StartEdge(edged, foundEdge);
            return;
        }
        var head = TitleAt(w, ray);
        if (head != null)
        {
            var panel = Panel(head.Window);
            if (panel.OnClose(head.Uv))
                w.Close(head.Window);
            else if (panel.TabAt(head.Uv.X) >= 0)
                ShowTab(head.Window, panel.TabAt(head.Uv.X));
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
            pressed = hit.Window;
            EditorHost.Press(hit.Window.Code.ViewId, hit.Uv.X * hit.Window.PxW, hit.Uv.Y * hit.Window.PxH);
            return;
        }
        if (pickGame && Paused)
            RequestPick(ray);
    }

    // ray が指している物に対する操作の案内を、右下の決まった場所に出す (画面への pass の外で呼ぶ)
    // LIDER_HINTS=0 なら、操作の案内を出さない
    static readonly bool hintsOff = Environment.GetEnvironmentVariable("LIDER_HINTS") == "0";

    static void ShowHints(Workspace w, Ray ray)
    {
        if (text == null)
            return;
        if (hintsOff)
        {
            // 操作の案内は出さないが、負荷の札は出す
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
        var head = uv == null ? TitleAt(w, ray) : null;
        closeHover = head != null && Panel(head.Window).OnClose(head.Uv) ? head.Window : null;
        var bar = InspectTitleAt(ray);
        if (bar != null && inspectTitle.OnClose(bar.Value.X))
            closeHover = inspectWindow;
        var edged = uv == null && lastEye == null && !Zen ? EdgeWindow(w, ray) : null;
        edgeHoverWindow = edged;
        edgeHover = foundEdge;
        if (edged != null)
            c.Target = HintTarget.Edge;
        else if (closeHover != null)
            c.Target = HintTarget.Close;
        else if (uv != null)
        {
            c.Target = HintTarget.Inspect;
            var row = inspect.Load ? null : inspect.RowAt(uv.Value);
            c.Openable = row != null && row.Openable && !row.Cycle;
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
            c.MoreMark = mark != null && mark.Id == "";
        }
        var hints = DetailHints(w, ray);
        foreach (var h in HintRules.For(c))
            hints.Add(h);
        hintCard.Prepare(hints, text);
        PlaceHints(w);
    }

    // マウスがゲームのものの間の案内: 指している物に依らない操作だけを出す
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

    // 案内のウィンドウを、札の字のある大きさにして、スクリーンの規則 (右下の隅) で置く
    // 画面のキーボードのウィンドウを、出している間だけスクリーンに載せ、大きさを絵に合わせる。最初に載せる所は desk で、その後は移した先に居る
    static void PlaceOsk(Workspace w)
    {
        var on = w.ScreenOf(oskWindow);
        if (on != null && !osk.Shown)
        {
            on.Windows.Remove(oskWindow);
            oskWindow.Position = null;
            oskWindow.ViewportRect = null;
            oskWindow.ContentRect = null;
            osk.HoverRow = -1;
            osk.HoverAt = -1;
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

    // ray が画面のキーボードを指している所 (絵の中の 0..1。指していなければ null)
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

    // ray の指す key を光らせる
    static void PointOsk(Workspace w, Ray ray)
    {
        var uv = OskUv(w, ray);
        osk.Point(uv != null ? uv.Value.X : -1, uv != null ? uv.Value.Y : -1);
    }

    // ray が画面のキーボードの key を指していれば、その key を押す (つないだキーボードと同じ event を流す)。キーボードを指していれば true
    public static bool PressOsk(Workspace w, Ray ray)
    {
        var uv = OskUv(w, ray);
        if (uv == null)
            return false;
        osk.Point(uv.Value.X, uv.Value.Y);
        var key = osk.Hovered;
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

    // 負荷の一覧の行か、窓の行の右の負荷を指しているなら、その行の負荷を文字だけの行で (案内の札の上に出す)
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

    // マウスとコントローラーがゲームのものの間は、指している物の案内と光を消す
    static void HideHints()
    {
        hintCard.Shown = false;
        closeHover = null;
        edgeHoverWindow = null;
        pointer = null;
        What = new List<string>();
    }

    static readonly int[] NoGaps = new int[0];

    // focus のある窓の左の、動いている版の板と窓との間の幅 (面の上のメートル、出していなければ 0)
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

    // インスペクタのウィンドウを、出しているならスクリーンに置き、しまっているなら外す
    static void PlaceInspector(Workspace w)
    {
        // 出す間だけスクリーンに載せる。最初に載せる所は、コードの窓を置くスクリーン。その後は、移した先のスクリーンにそのまま居る
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
            // コードのスクリーン (横一列) では一番左に、ほかのスクリーンでは最後に載せる
            if (home == w.CodeScreen)
                home.Windows.Insert(0, inspectWindow);
            else
                home.Windows.Add(inspectWindow);
            on = home;
        }
        // 範囲を決めている間は、focus のある窓のすぐ左に並べる (範囲にこの 2 つを収める)
        if (on == w.CodeScreen && AreaW > 0 && w.FocusWindow != null && w.ScreenOf(w.FocusWindow) == on)
        {
            on.Windows.Remove(inspectWindow);
            on.Windows.Insert(on.Windows.IndexOf(w.FocusWindow), inspectWindow);
        }
        // コードのスクリーンの真ん中には、最初のコードの窓を置く (インスペクタはその左に並ぶ)。
        // 視点に固定している間は、視点を横へ送れないので、focus のあるウィンドウを真ん中に置く
        w.CodeScreen.Origin = null;
        if (w.CodeScreen.Anchor == ScreenAnchor.View && w.Focused != null && w.CodeScreen.Windows.Contains(w.Focused))
            w.CodeScreen.Origin = w.Focused;
        foreach (var window in w.CodeScreen.Windows)
            if (window.Content is Code && w.CodeScreen.Origin == null)
                w.CodeScreen.Origin = window;
        // 横一列では、インスペクタの画素の数と倍率が大きさを決める
        inspectWindow.PxW = InspectView.PxW;
        inspectWindow.PxH = InspectView.PxH;
        inspectWindow.Scale = InspectScale;
        // 平面のスクリーンでは、配置の規則が決めた矩形に収める
        if (on != null && OnPlane(on) && inspectWindow.Position != null)
            InspectView.SetSize(inspectWindow.Size.X * ViewScale(on) / InspectScale, inspectWindow.Size.Y * ViewScale(on) / InspectScale - PanelView.TitleH);
        // 隠れたウィンドウには focus を置かない
        if (w.Focused == inspectWindow && !TreeVisible)
            w.FocusCode();
    }

    // インスペクタの中心の角度 (インスペクタの所を押すテストが使う)、高さ、大きさ (半径 Space.R の所のメートル)
    public static float BoardAngle => Work != null ? WindowAngle(Work, inspectWindow) : 0;

    static float InspectY()
    {
        return Work != null ? WindowY(Work, inspectWindow) : 0;
    }

    static Vec2 InspectSize()
    {
        var screen = Work != null ? ScreenFor(Work, inspectWindow) : null;
        // 平面のスクリーンでは、ウィンドウの矩形に見出しも収める
        return screen != null && OnPlane(screen)
            ? new Vec2(inspectWindow.Size.X * ViewScale(screen), inspectWindow.Size.Y * ViewScale(screen) - InspectTitleH)
            : inspectWindow.Size;
    }

    // インスペクタの見出しの帯の高さと、中心の高さ
    static float InspectTitleH => PanelView.TitleH * InspectScale;

    static float InspectTitleY()
    {
        return InspectY() + (InspectSize().Y + InspectTitleH) * 0.5f;
    }

    // インスペクタの見出しの帯を指していれば、その中の位置
    static Vec2? InspectTitleAt(Ray ray)
    {
        var w = Work;
        if (w == null || !TreeVisible)
            return null;
        return HitOn(ScreenFor(w, inspectWindow), ray, BoardAngle, InspectTitleY(), InspectSize().X, InspectTitleH);
    }

    // インスペクタを指していれば、その中の位置
    static Vec2? InspectAt(Ray ray)
    {
        var w = Work;
        if (w == null || !TreeVisible)
            return null;
        var size = InspectSize();
        return HitOn(ScreenFor(w, inspectWindow), ray, BoardAngle, InspectY(), size.X, size.Y);
    }

    // 押した窓の上を指していれば、押した所からそこまでを選ぶ
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
        // 保存した版のゲームの絵を指しているときだけ調べる (XR ではゲームを空間に描いていないので、指せない)
        if (lastEye != null)
            return;
        PointGames(pickX, pickY);
        if (gamePointed == gameWindow)
            GameApp.Pick(gameUv);
    }

    // 今の画面に合う密度の、窓の本文の文字
    static MonoText PanelText()
    {
        var density = 1;
        if (Space.Flat)
        {
            Gfx.Size(out _, out var sh);
            // web の Gfx.Size は CSS px なので、ページが画素の比率を渡す
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
        return (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/yutapon_coding_081d.ttf";
    }

    // 窓の view ごとの板
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

    // window を置いているスクリーン
    static Screen ScreenFor(Workspace w, Window window)
    {
        return w.ScreenOf(window) ?? w.CodeScreen;
    }

    // 平面のスクリーンか (固定先によらない)。
    // その上の物は、半径 Space.R の所に、見る人の正面から同じ見かけの大きさになるように描いて当て、Pose でスクリーンの距離へ運ぶ
    static bool OnPlane(Screen screen)
    {
        return screen.Shape == ScreenShape.Plane;
    }

    // 視点の座標の view-projection (非 XR は既定の画角のカメラ、XR は描いている眼) と、視点の座標から lider の空間への行列 (非 XR は単位行列)
    static Mat4 viewVp = Mat4.Identity();
    static Mat4 flatViewVp = Mat4.Identity();
    static Mat4 headToWorld = Mat4.Identity();

    // lider の空間の ray を、screen の上の窓を当てる ray にする (視点に固定したスクリーンなら視点の座標の ray)
    static Ray RayOn(Screen screen, Ray ray)
    {
        if (screen.Anchor == ScreenAnchor.View)
        {
            if (lastEye != null)
            {
                var toHead = headToWorld.Inverse();
                ray = new Ray(toHead.MulPoint(ray.Origin), toHead.MulDir(ray.Dir));
            }
            else
            {
                // 画面の同じ画素を通る、視点の座標の ray
                var px = ViewportOf(lastVp, ray.At(1));
                ray = Camera3d.ViewportRay(flatViewVp, px.X, px.Y, viewportW, viewportH);
            }
        }
        var back = Pose(screen).Inverse();
        return new Ray(back.MulPoint(ray.Origin), back.MulDir(ray.Dir));
    }

    // screen を、基準の置き方 (円筒の軸が固定先の原点を通り、高さが 0) から、今の位置へ運ぶ行列。
    // スクリーンの上の物は、基準の置き方の座標で描いて当て、この行列で運ぶ。
    // 平面のスクリーンは、位置を面の上の座標に入れて半径 Space.R の所に描いているので、原点から距離の比で広げて、スクリーンの距離へ運ぶ
    static Mat4 Pose(Screen screen)
    {
        if (OnPlane(screen))
        {
            var far = screen.Position.Z / Space.R;
            return Mat4.Scale(new Vec3(far, far, far));
        }
        var deeper = screen.Radius - Space.R;
        // 円筒を平面に開いている間は、奥へまっすぐ運ぶ
        if (!Xr.Active())
            return Mat4.Translate(new Vec3(0, screen.Position.Y, deeper));
        return Mat4.Translate(new Vec3(deeper * (float)Math.Sin(screen.Angle), screen.Position.Y, deeper * (float)Math.Cos(screen.Angle)));
    }

    // screen の上の物を描く view-projection (world は、世界の座標の view-projection)
    static Mat4 VpOf(Screen screen, Mat4 world)
    {
        return (screen.Anchor == ScreenAnchor.View ? viewVp : world) * Pose(screen);
    }

    // screen の上の、角度 angle、高さ y を中心にした幅 w、高さ h の板を ray で指した所 (板の中の 0..1)。
    // 平面のスクリーンは XR でも曲げない
    static Vec2? HitOn(Screen screen, Ray ray, float angle, float y, float w, float h)
    {
        var flat = Space.Flat;
        Space.Flat = flat || screen.Shape == ScreenShape.Plane;
        var uv = Space.HitCurved(RayOn(screen, ray), angle, y, Space.R, w, h);
        Space.Flat = flat;
        return uv;
    }

    // 平面のスクリーン (距離 Position.Z) の上の長さを、半径 Space.R の所の同じ見かけの長さにする倍率
    static float ViewScale(Screen screen)
    {
        return Space.R / screen.Position.Z;
    }

    // コードの窓の、円筒の角度 (まだ並べていなければ並べる)
    public static float WindowAngle(Workspace w, Window window)
    {
        if (window.Position == null)
            w.Arrange(grab.Window);
        if (window.Position == null)
            return 0;
        var screen = ScreenFor(w, window);
        return OnPlane(screen) ? (screen.Position.X + window.Position.Value.X) / screen.Position.Z : screen.AngleAt(window.Position.Value.X);
    }

    // コードのウィンドウの子の、動いている版を左右に並べる板 (無ければ null)
    public static Window? DiffOf(Window window)
    {
        foreach (var child in window.Children)
            if (child.Content is Diff)
                return child;
        return null;
    }

    // 動いている版の板を、出す間だけコードのウィンドウの子にし、親の左の手前に置く (親の並びは変えない)
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

    // 平面のスクリーンで、ウィンドウの矩形の上の端に、本文と別に描く見出しの帯を収めるウィンドウか
    static bool TitleInRect(Window window)
    {
        return window.Content is Code || window.Content is Inspector;
    }

    // コードの窓の本文の真ん中の高さ (横一列に並べた窓は 0。つかんでいる間だけ動く)
    public static float WindowY(Workspace w, Window window)
    {
        if (window.Position == null)
            w.Arrange(grab.Window);
        if (window.Position == null)
            return 0;
        var screen = ScreenFor(w, window);
        // 平面のスクリーンでは、ウィンドウの矩形に見出しも収めるので、本文は見出しの分だけ下がる
        return OnPlane(screen)
            ? (screen.Position.Y + window.Position.Value.Y) * ViewScale(screen) - (TitleInRect(window) ? PanelView.TitleH * window.Scale * 0.5f : 0)
            : window.Position.Value.Y;
    }

    // ray が当たる窓 (focus のある窓を先に見る)
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

    // ray が window に当たる所 (窓の中の 0..1)
    static Vec2? HitWindow(Workspace w, Window window, Ray ray)
    {
        // 動いている間は、見えている所を指す
        var m = MotionOf(window);
        var scale = m != null ? m.Scale.Value : window.Scale;
        return HitOn(ScreenFor(w, window), ray, m != null ? m.Angle.Value : WindowAngle(w, window), m != null ? m.Y.Value : WindowY(w, window), window.UnscaledSize.X * scale, window.UnscaledSize.Y * scale);
    }

    // ray が当たる窓の見出しの帯 (focus のある窓を先に見る)
    static WindowHit? TitleAt(Workspace w, Ray ray)
    {
        if (w.FocusWindow != null)
        {
            var uv = HitTitle(w, w.FocusWindow, ray);
            if (uv != null)
                return new WindowHit(w.FocusWindow, uv.Value);
        }
        foreach (var window in w.Windows)
        {
            var uv = HitTitle(w, window, ray);
            if (uv != null)
                return new WindowHit(window, uv.Value);
        }
        return null;
    }

    static Vec2? HitTitle(Workspace w, Window window, Ray ray)
    {
        var m = MotionOf(window);
        var scale = m != null ? m.Scale.Value : window.Scale;
        return HitOn(ScreenFor(w, window), ray, m != null ? m.Angle.Value : WindowAngle(w, window), Panel(window).TitleY(m != null ? m.Y.Value : WindowY(w, window), scale),
            window.UnscaledSize.X * scale, PanelView.TitleH * scale);
    }

    // Gfx.Pick の結果が届いたら、click なら呼び出し元の並びのうち一覧にある最初の file をその行で開き、指しているだけなら What にする
    static void TakePick(Workspace w)
    {
        if (!pickPending)
            return;
        var hit = GameApp.PickResult();
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
            // それを描いた個体 (開いた行の段の this) に絞る
            if (i < selves.Length && selves[i] != "")
                ScopeToInstance(selves[i]);
            return;
        }
    }

    // 呼び出し元の並びのうち一覧にあるものを、「メソッド (file:行)」の行にする (内側から MaxWhat 個)
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

    // 画面の (x, y) に何が描いてあるかを調べる (click しないで指しているとき)
    public static void HoverAt(float x, float y)
    {
        pickX = x;
        pickY = y;
        RequestPick(PointerRay(x, y));
        hoverPick = true;
    }

    // マウスが止まってゲームの物を指している間、HoverSeconds ごとに何が描いてあるかを調べる。ほかを指すか動かすと消す
    static void Hover(Workspace w, Ray ray, float mx, float my, float dt)
    {
        var moved = mx != hoverX || my != hoverY;
        hoverX = mx;
        hoverY = my;
        var game = Paused && InspectAt(ray) == null && InspectTitleAt(ray) == null && TitleAt(w, ray) == null && WindowAt(w, ray) == null && !Search.Shown;
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

    // XR でないときの、画面の大きさと、視点に固定した物を描くカメラを決める。
    // lider の物をまだ描いていなければ (起動してからゲームの絵だけを出している間)、世界を見るカメラは、視点を送っていない既定の物にする
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
        // スクリーンは、拡大 (fov) に付いていかない既定の画角で置く
        PutAway();
        backdrop.Fill(Space.FlatFov, (float)sw / sh);
        hud.Fill(Space.FlatFov, (float)sw / sh);
        palette.Fill(Space.FlatFov, (float)sw / sh);
        foreach (var screen in Screens)
            if (OnPlane(screen))
            {
                SetGameTitles(screen);
                if (xr)
                {
                    // XR には画面が無いので、既定の画角で見たときの矩形にする (ウィンドウの中の割り付けに使う)
                    var perM = sh / (2 * screen.Position.Z * (float)Math.Tan(Space.FlatFov * 0.5f * Math.PI / 180));
                    screen.Project(new Vec2(sw * 0.5f + screen.Position.X * perM, sh * 0.5f - screen.Position.Y * perM), perM);
                }
                else
                {
                    // スクリーンの面の真ん中と、その 1 メートル右が、画面のどこに写るか
                    var origin = SurfacePx(screen, 0, 0);
                    screen.Project(origin, SurfacePx(screen, 1, 0).X - origin.X);
                }
            }
        // Space に置いたゲームは、XR でなければ、画面いっぱいの絵に描かせる (XR では、ゲームが眼に直に描く)
        var spaced = Work?.InSpace;
        if (spaced != null)
        {
            spaced.TitleBar = 0;
            spaced.ViewportRect = Xr.Active() ? null : new PixelRect(0, 0, sw, sh);
            spaced.ContentRect = spaced.ViewportRect;
        }
        // Space を見ているカメラ (前の frame の物) を、Xr.View と同じ書き方 (前方 -Z) にして渡す
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
        // 入力は、マウスのあるゲームのウィンドウの絵の中の位置で、どちらのゲームにも渡す
        Input.MousePos(out var mx, out var my);
        PointGames(mx, my);
        gameCloseHover = null;
        gameSpaceHover = null;
        var over = GameTitleAt(mx, my);
        if (over != null && OnGameClose(over, mx))
            gameCloseHover = over;
        else if (over != null && OnGameSpace(over, mx))
            gameSpaceHover = over;
        // キーボードは focus がゲームにあるとき、マウスはゲームを指しているときだけ、ゲームに見せる
        var keyboard = Work == null || Work.OnGame;
        var mouse = Work == null || MouseForGame(Work, mx, my);
        if (saved != null)
            GameApp.Step(dt, keyboard, mouse, GamePxW(saved), GamePxH(saved));
        if (draft != null && Work != null)
            DraftGame.Step(dt, keyboard, mouse, GameApp.Position >= 0, GamePxW(draft), GamePxH(draft), Work.Docs);
        // XR には画面が無い (左右の眼にだけ描ける) ので、画面に貼る絵は、眼への pass の中で空間の面として描く (DrawScene)
        var flat = !xr;
        if (flat)
            ScreenView.Draw(Screens, sw, sh, spaced?.Content.Texture());
        StepTimeline(dt);
        if (flat)
            DrawGameTitles(sw, sh);
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

    // version のゲームの絵を映している (画面に出ている) ウィンドウ
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

    // ゲームの絵を描く画素の数。画面に貼った平面のスクリーンでは画面の上の矩形の画素、ほかのスクリーンではウィンドウの大きさに合う画素
    static int GamePxW(Window window)
    {
        return (int)((window.ContentRect != null ? window.ContentRect.W : window.Size.X * PanelView.BasePxW / PanelView.BaseW) * PixelRatio);
    }

    static int GamePxH(Window window)
    {
        return (int)((window.ContentRect != null ? window.ContentRect.H : window.Size.Y * PanelView.BasePxW / PanelView.BaseW) * PixelRatio);
    }

    // マウスが最後に指した、ゲームの絵のウィンドウ (指していなければ null) と、絵の中の位置、絵の画面の上の幅
    static Window? gamePointed;
    static Vec2 gameUv = new Vec2(0.5f, 0.5f);
    static float gameScreenW = 1;

    // 画面の (x, y) が指している、ゲームの絵の中の位置を、どちらのゲームにも渡す。
    // どの絵も指していなければ、画面に貼った保存した版の絵の外の位置にする (空間に置いた絵なら、最後に指した位置のまま)
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

    // 画面の (x, y) が window のゲームの絵を指していれば、絵の中の位置 (左上が 0,0)。指した絵の画面の上の幅を gameScreenW に入れる
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

    // ゲームの絵のウィンドウの見出しの帯 (版の名前と、編集中の版には閉じる印)。
    // スクリーンに出ているウィンドウが 1 枚だけなら出さない (画面いっぱいのゲームの絵に帯を乗せない)
    static MonoText? labelText;
    static SpriteBatch? labelBatch;
    // 閉じる印を指しているゲームの絵のウィンドウ
    static Window? gameCloseHover;

    static void SetGameTitles(Screen screen)
    {
        screen.Arrange();
        var shown = 0;
        foreach (var window in screen.Windows)
            if (window.Position != null)
                shown++;
        foreach (var window in screen.Windows)
            // 1 枚だけのときも、Alt を押している間 (ウィンドウとスクリーンへの操作の間) は帯を出す
            if (window.Content is GameView)
                window.TitleBar = shown > 1 || ScreensLit ? PanelView.TitlePx : 0;
    }

    // 画面の (x, y) にある、ゲームの絵のウィンドウの見出しの帯 (無ければ null)
    static Window? GameTitleAt(float x, float y)
    {
        foreach (var screen in Screens)
            if (screen.Shown)
                foreach (var window in screen.Windows)
                    if (window.Content is GameView && window.TitleBar > 0 && window.ViewportRect != null
                        && window.ViewportRect.Contains(x, y) && y < window.ViewportRect.Y + window.TitleBar)
                        return window;
        return null;
    }

    // 閉じられるゲームの絵のウィンドウか (保存した版は閉じられない)
    static bool Closable(Window window)
    {
        return window.Content is GameView view && view.Version == GameVersion.Draft;
    }

    // 見出しの帯の x が閉じる印の上か
    static bool OnGameClose(Window window, float x)
    {
        var at = window.ViewportRect!;
        return Closable(window) && x >= at.X + at.W - window.TitleBar;
    }

    // Space へ出す印の左の端 (閉じる印があれば、その左に並べる)
    static float GameSpaceMarkX(Window window)
    {
        var at = window.ViewportRect!;
        return at.X + at.W - window.TitleBar * (Closable(window) ? 2 : 1);
    }

    // 見出しの帯の x が、Space へ出す印の上か
    static bool OnGameSpace(Window window, float x)
    {
        var left = GameSpaceMarkX(window);
        return x >= left && x < left + window.TitleBar;
    }

    // Space へ出す印を指しているゲームの絵のウィンドウ
    static Window? gameSpaceHover;

    static string GameTitle(Window window)
    {
        if (Closable(window))
            return DraftGame.Label();
        return DraftGame.Shown ? "保存済み" : "Game";
    }

    static void DrawGameTitles(int sw, int sh)
    {
        var any = TimelineRect() != null;
        foreach (var screen in Screens)
            foreach (var window in screen.Windows)
                any = any || (window.Content is GameView && window.TitleBar > 0 && window.ViewportRect != null);
        if (!any)
            return;
        var density = Math.Max(1, (int)Math.Round(PixelRatio));
        if (labelText == null || labelText.Density != density)
            labelText = new MonoText("lider_label_" + density, Ttf(), 15, density);
        if (labelBatch == null || labelBatch.LogicalW != sw || labelBatch.LogicalH != sh)
            labelBatch = new SpriteBatch("lider_label", sw, sh);
        var t = labelText;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        labelBatch.Begin();
        foreach (var screen in Screens)
            foreach (var window in screen.Windows)
            {
                var at = window.ViewportRect;
                if (!(window.Content is GameView) || window.TitleBar <= 0 || at == null)
                    continue;
                var bar = window.TitleBar;
                var ink = Work != null && Work.Focused == window ? Theme.Ink : Theme.Muted;
                var y = at.Y + (bar - t.LineH) * 0.5f;
                labelBatch.Rect(at.X, at.Y, at.W, bar, Theme.Surface);
                if (Closable(window))
                    labelBatch.Rect(at.X + at.W - bar + 3, at.Y + 3, bar - 6, bar - 6, Color.Rgb(0.9f, 0.3f, 0.35f, window == gameCloseHover ? 1 : 0));
                var mark = GameSpaceMarkX(window);
                labelBatch.Rect(mark + 3, at.Y + 3, bar - 6, bar - 6, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, window == gameSpaceHover ? 0.8f : 0));
                t.Draw(labelBatch, GameTitle(window), at.X + 4 + t.CellW, y, ink);
                if (Closable(window))
                    t.Draw(labelBatch, "×", at.X + at.W - (bar + t.CellW) * 0.5f, y, ink);
                // Space へ出す印
                t.Draw(labelBatch, "↑", mark + (bar - t.CellW * 2) * 0.5f, y, ink);
            }
        DrawTimeline();
        labelBatch.Flush();
        Gfx.EndPass();
    }

    // ゲームの OnEvent に event を渡す (並べている間は、編集中の版にも)
    static void SendGame(EventData e)
    {
        if (e.Kind == EventKind.MouseButtonDown || e.Kind == EventKind.MouseButtonUp || e.Kind == EventKind.MouseMotion)
            PointGames(e.X, e.Y);
        GameApp.Send(e);
        DraftGame.Send(e);
    }

    // 保存した版と編集中の版を並べる / 戻す (背景のスクリーンを左右に等分する)
    public static void ToggleCompare()
    {
        if (DraftGame.Shown)
        {
            DraftGame.Close();
            // 閉じるウィンドウに focus があったら、保存した版のウィンドウに移す
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
        // ゲームの絵は、コードの窓より前に並べる
        backdrop.Windows.Insert(1, draftWindow);
    }

    // コードの窓を、ゲームの絵と同じスクリーン (backdrop) に並べているか。
    // false なら、コードの窓は手前のコードのスクリーンにあり、ゲームの絵に透けて重なる
    public static bool Tiled => Work != null && Work.Home == backdrop;

    // 配置: 初めから入っている 2 つ (重ねる形、左右に並ぶ形) と、利用者が控えた物。名前で引く
    public static readonly List<Arrangement> Arrangements = new List<Arrangement>();
    // 利用者が控えた配置 (file に書く物)
    static readonly List<Arrangement> savedArrangements = new List<Arrangement>();
    // 最後に戻した配置の名前
    public static string ArrangementName = "";
    // 控えた配置の file を読み終えたか
    static bool arrangementsLoaded;
    public const string Overlay = "overlay";
    public const string SideBySide = "side-by-side";

    static string ArrangementsPath(Workspace w)
    {
        return w.Root + "/.lider/arrangements.txt";
    }

    // 初めから入っている配置を作る (起動したときの、スクリーンの形と位置から)
    static void BuiltinArrangements(Workspace w)
    {
        Arrangements.Clear();
        savedArrangements.Clear();
        arrangementsLoaded = false;
        // 重ねる形: ゲームの絵が画面いっぱいで、手前のコードのスクリーンに、インスペクタとコードの窓が透けて重なる
        var overlay = Arrangement.Capture(Overlay, w);
        // 左右に並ぶ形: 左にゲームの絵、右にコードの窓とインスペクタを積む
        var side = Arrangement.Capture(SideBySide, w);
        foreach (var a in new List<Arrangement> { overlay, side })
            foreach (var spec in a.Screens)
            {
                spec.Windows.Clear();
                var tiled = a == side;
                if (spec.Name == backdrop.Name)
                {
                    // 左右に並ぶ形は、世界に置く (XR で頭に付いてこず、頭を回して端まで見られる)
                    if (tiled)
                        spec.Anchor = ScreenAnchor.World;
                    spec.Layout = tiled ? "MainStack 0.5" : "Even";
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

    // 配置ごとに、その配置に戻す命令を載せる
    static void RegisterArrangements()
    {
        foreach (var a in Arrangements)
        {
            var name = a.Name;
            CommandTable.Register("arrange:" + name, "配置「" + name + "」に戻す", () => With(w => Arrange(name)));
        }
    }

    // 控えた配置の file を、読めるようになったら 1 度だけ読む。同じ名前の配置は、控えた物に置き換える
    // key の割り当て: 利用者が変えた分を、利用者ごとの file に書く (プロジェクトによらない)。
    // file は env LIDER_KEYS、無ければ XDG_CONFIG_HOME か ~/.config の lider/keys.txt。場所が決まらなければ、変えた分は lider を終えるまで
    static bool keysLoaded;
    // 次に押した key を割り当てる命令 (待っていなければ null)
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
        // focus をゲームから戻す key は、いつも 1 つは要る
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

    // key を待っている間に押した key を、命令 keyFor に割り当てる。Esc はやめる。割り当てられない key は、理由を出して待ち続ける
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
        // focus をゲームから戻す key を、ほかの命令に取らせない
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

    // 控えた配置を一覧に入れる (同じ名前の物は置き換える)
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

    // 名前の配置に戻す (無ければ false)
    public static bool Arrange(string name)
    {
        var w = Work;
        if (w == null)
            return false;
        foreach (var a in Arrangements)
        {
            if (a.Name != name)
                continue;
            // 出しているインスペクタも、配置の決めた所に並べる
            PlaceInspector(w);
            a.Apply(w);
            ArrangementName = name;
            // 視点の横送りは、配置を替えたら戻す
            viewTarget = 0;
            viewX.Snap(0);
            followed = null;
            // 移した先では、開いたときと同じに小さな大きさから広がる
            motions.Clear();
            PlaceInspector(w);
            return true;
        }
        return false;
    }

    // 一覧の、今の配置の次の配置に戻す
    public static void NextArrangement()
    {
        var at = 0;
        for (var i = 0; i < Arrangements.Count; i++)
            if (Arrangements[i].Name == ArrangementName)
                at = i;
        if (Arrangements.Count > 0)
            Arrange(Arrangements[(at + 1) % Arrangements.Count].Name);
    }

    // 今の配置を name で控え、file に書く (同じ名前の配置は上書きする)
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

    // window を、次のスクリーンへ移す (ウィンドウを並べるスクリーンを順に回る)。
    // 端から積む規則のスクリーン (案内、検索) は、決まった物を出す所なので、移す先にしない
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
            // 移した先では、開いたときと同じに小さな大きさから広がる
            motions.Remove(window);
            if (window.Content is Code)
                w.Home = next == w.CodeScreen ? null : next;
            return;
        }
    }

    // 窓の大きさを Zen に合わせる。画面に 1 枚が収まる px は、窓の px の大きさ (倍率) と画角で決まる
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
            // 平面のスクリーンでは、配置の規則が決めた矩形に、見出しと本文をちょうど収める
            var screen = ScreenFor(w, window);
            if (OnPlane(screen) && window.Position != null)
            {
                var perPx = PanelView.BaseW / PanelView.BasePxW * window.Scale / ViewScale(screen);
                // 矩形からはみ出さないよう、利用者の決める大きさの下限 (Window.MinPxW) は当てない
                window.PxW = Math.Max(1, (int)(window.Size.X / perPx));
                window.PxH = Math.Max(1, (int)(window.Size.Y / perPx) - PanelView.TitlePx);
            }
            var panel = Panel(window);
            panel.PxW = window.PxW;
            panel.PxH = window.PxH;
        }
    }

    // 指しているか引いている端を光らせる
    static void DrawEdge(Workspace w, Mat4 vp)
    {
        var window = sizing ?? edgeHoverWindow;
        var edge = sizing != null ? sizingEdge : edgeHover;
        var m = window != null ? MotionOf(window) : null;
        if (window == null || m == null)
            return;
        var color = Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.9f);
        var r = Space.R - PanelView.FrontGap - 0.006f;
        var k = r / Space.R;
        var bw = window.UnscaledSize.X * m.Scale.Value;
        var bh = window.UnscaledSize.Y * m.Scale.Value;
        if ((edge & 1) != 0)
            Quad3d.CurvedSolid(vp, color, m.Angle.Value + (bw * 0.5f + 0.004f) / Space.R, m.Y.Value * k, r, 0.005f, bh * k);
        if ((edge & 2) != 0)
            Quad3d.CurvedSolid(vp, color, m.Angle.Value, (m.Y.Value - bh * 0.5f - 0.004f) * k, r, bw * k, 0.005f);
    }

    // 窓の端の、引いて大きさを変える所の幅 (メートル。窓の外側)
    const float EdgeW = 0.018f;

    // 指した窓の端。右なら 1、下なら 2 の和 (当たらなければ 0)
    public static int EdgeAt(Workspace w, Window window, Ray ray)
    {
        // 端を引いて大きさを変えられるのは、自分の大きさを持つ窓 (横一列のコードのスクリーン) だけ
        if (grab.Holding || ScreenFor(w, window) != w.CodeScreen)
            return 0;
        var m = MotionOf(window);
        var scale = m != null ? m.Scale.Value : window.Scale;
        var angle = m != null ? m.Angle.Value : WindowAngle(w, window);
        var y = m != null ? m.Y.Value : WindowY(w, window);
        var bw = window.UnscaledSize.X * scale;
        var bh = window.UnscaledSize.Y * scale;
        var uv = Space.HitCurved(RayOn(w.CodeScreen, ray), angle + EdgeW * 0.5f / Space.R, y - EdgeW * 0.5f, Space.R, bw + EdgeW, bh + EdgeW);
        if (uv == null)
            return 0;
        var edge = 0;
        if (uv.Value.X * (bw + EdgeW) >= bw)
            edge += 1;
        if (uv.Value.Y * (bh + EdgeW) >= bh)
            edge += 2;
        return edge;
    }

    // 窓の外側の右か下の端を探す (focus のある窓を先に見る)。端の向きは foundEdge に置く
    static Window? EdgeWindow(Workspace w, Ray ray)
    {
        foundEdge = 0;
        if (w.FocusWindow != null)
        {
            foundEdge = EdgeAt(w, w.FocusWindow, ray);
            if (foundEdge != 0)
                return w.FocusWindow;
        }
        foreach (var window in w.Windows)
        {
            foundEdge = EdgeAt(w, window, ray);
            if (foundEdge != 0)
                return window;
        }
        return null;
    }

    // ray の指す所まで、引いている端を動かす
    public static void DragEdge(Workspace w, Ray ray)
    {
        var window = sizing;
        if (window == null || Zen)
            return;
        var at = Space.OnCylinderAt(RayOn(w.CodeScreen, ray), Space.R);
        if (at == null)
            return;
        var perPx = PanelView.BaseW / PanelView.BasePxW * window.Scale;
        var pxW = window.WidthPx;
        var pxH = window.HeightPx;
        if ((sizingEdge & 1) != 0)
        {
            // 先頭の窓は真ん中が動かないので、端までの差の 2 倍だけ広げる
            var right = WindowAngle(w, window) * Space.R + window.UnscaledSize.X * window.Scale * 0.5f;
            var factor = w.CodeScreen.Windows.IndexOf(window) == 0 ? 2 : 1;
            pxW += (int)((at.Angle * Space.R - right) * factor / perPx);
        }
        if ((sizingEdge & 2) != 0)
        {
            var bottom = WindowY(w, window) - window.UnscaledSize.Y * window.Scale * 0.5f;
            pxH += (int)((bottom - at.Y) * 2 / perPx);
        }
        window.Resize(pxW, pxH);
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

    // Zen の窓が画面に占める割合
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
        // 検索の欄を閉じたら、key を待つのもやめる
        if (!Search.Shown)
            keyFor = null;
        PlaceInspector(w);
        // 記録は lider を出していない間も受け取って捨てる (溜めない)
        if (inspect.Tree.ScopeId != tracedScope)
        {
            tracedScope = inspect.Tree.ScopeId;
            w.Trace.ForgetLines();
        }
        w.Trace.Take(tracedScope != "" ? InspectTree.ScopePin : "");
        Costs.Refresh();
        Costs.Viewing = inspect.Shown && inspect.Load;
        // プロファイルを見ている間は、行番号の所に負荷の数を出す (行番号は描かず、欄は数の 4 桁が入る幅にする)
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
            // エディタの幅と行の高さを、lider の桁に合わせる
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
        // 窓のフィールドとインスペクタのメソッドの宣言を拾う
        if (fieldFrames++ % ScanFrames == 0)
            Search.Refresh(w);
        // texture の描き直しは画面への pass の外で済ませる
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
            var motion = MotionOf(window);
            panel.TabLabels = w.TabTitles(window);
            panel.Tab = window.Code.Tab;
            panel.Title = motion != null && motion.LandLabel > 0 ? (int)(motion.LandSeconds * 100) / 100.0f + "s" : "";
            panel.Focused = window == w.Focused;
            panel.Costs = Costs;
            panel.Fields = Fields.For(Search.DeclarationsIn(window.Code.Doc.Path), EditorHost.FirstLine(window.Code.ViewId), EditorHost.LastLine(window.Code.ViewId));
            panel.CloseLit = window == closeHover;
            var differs = window == w.FocusWindow && window.Code.Doc.Differs();
            panel.OldShown = differs && PanelView.SideBySide && !ZenShown;
            PlaceDiff(window, panel.OldShown);
            panel.Inline = differs && !PanelView.SideBySide;
            EditorHost.SetGaps(window.Code.ViewId, panel.Inline ? window.Code.Doc.InlineGaps() : NoGaps);
            panel.Prepare(window.Code.Doc, panelText, w.Trace);
        }
        w.Arrange(grab.Window);
        // 閉じる動きの間の窓は描き直さないが、描くので texture を宣言し続ける
        foreach (var pair in motions)
            if (!w.Windows.Contains(pair.Key))
                Panel(pair.Key).Declare(panelText);
        // 窓の本文と別の文字でも、案内と inspect が使うので atlas を残す
        text.Glyphs.Ensure();
        if (Search.Shown || inspect.Shown)
            Search.Refresh(w);
        inspect.Prepare(panelText, w, Search, Fields, Costs);
        inspectTitle.Label = inspect.Load ? "Load" : "Inspector";
        inspectTitle.Focused = w.Focused == inspectWindow;
        inspectTitle.CloseLit = closeHover == inspectWindow;
        inspectTitle.PxW = InspectView.PxW;
        if (inspect.Shown)
            inspectTitle.Prepare(panelText);
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
            // 指す ray は、押している手、右手、左手の順に、取れた物
            var aim = (activeHand >= 0 ? HandRay(activeHand) : null) ?? HandRay(1) ?? HandRay(0) ?? gaze;
            for (var h = 0; h < 2; h++)
                AimAt(h, HandRay(h));
            // 左手の Y で、画面のキーボードを出し入れする
            var leftHand = Xr.Input(0);
            var y = leftHand != null && leftHand.Secondary;
            if (y && !oskButton)
                osk.Shown = !osk.Shown;
            oskButton = y;
            // 画面のキーボードは、どちらの手で指しても光る
            PointOsk(w, aim);
            for (var h = 0; h < 2 && osk.Hovered == null; h++)
            {
                var ray = HandRay(h);
                if (ray != null)
                    PointOsk(w, ray.Value);
            }
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
        // 視点の横送りと、画面に入る幅は、コードのスクリーンの面の上のメートルで数える
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
        // 視点を動かすと層の間に視差が出るので、視点は原点のまま、奥行きに比例して横へずらす (どの層も同じ角度だけずれる)。
        // 範囲を決めていれば、縦も範囲の真ん中に合わせる
        var slide = Mat4.Identity();
        slide.M02 = -viewX.Value / depth;
        if (AreaW > 0)
        {
            // 窓の見出しは本文の上に乗るので、見出しも入れた高さの真ん中 (面の上の高さ TitleH / 2) を範囲の真ん中に合わせる
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
        PointOsk(w, PointerRay(mx, my));
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

    // インスペクタと focus のある窓を置く画面の範囲を決める (w が 0 なら決めない)
    public static void SetArea(float x, float y, float w, float h)
    {
        AreaX = x;
        AreaY = y;
        AreaW = w;
        AreaH = h;
    }

    // 範囲に収まるよう、インスペクタと窓の大きさと視点を決める。
    // pxPerM は面 (半径 R) の上の 1 メートルの画素。インスペクタは手前 (InspectR) にあるので R / InspectR 倍に見える
    static void FitArea(Workspace w, float pxPerM)
    {
        // インスペクタは、範囲の左に、窓の本文と同じ高さで置く
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
            // 窓が無ければ、インスペクタの左の端を範囲の左の端に合わせる
            if (TreeVisible)
            {
                viewTarget = BoardAngle * Space.R - InspectSize().X * 0.5f - (AreaX - viewportW * 0.5f) / pxPerM;
                viewX.Snap(viewTarget);
            }
            return;
        }
        if (grab.Holding)
            return;
        // 窓の右の端を範囲の右の端に合わせる
        var right = WindowAngle(w, focus) * Space.R + focus.UnscaledSize.X * focus.Scale * 0.5f;
        viewTarget = right - (AreaX + AreaW - viewportW * 0.5f) / pxPerM;
    }

    // focus が移るか窓が動いたら、focus のある窓が見える所まで視点の行き先を動かす (列を動かした後は、次に動くまで追わない)
    static void FollowFocus(Workspace w, float halfViewport)
    {
        var window = w.FocusWindow;
        // コードの窓が無ければ、インスペクタを画面の左の端に寄せる
        if (window == null && TreeVisible && ScreenFor(w, inspectWindow) == w.CodeScreen)
            viewTarget = BoardAngle * Space.R - InspectSize().X * 0.5f - FollowMargin + halfViewport;
        if (window == null || grab.Holding || ScreenFor(w, window) != w.CodeScreen)
            return;
        var angle = WindowAngle(w, window);
        // インスペクタが focus のある窓のすぐ左に並んでいれば、インスペクタも見える所へ動かす (その左の端。並んでいなければ 0)
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
        // 窓の左の、動いている版の板も入れる
        var left = beside ? Math.Min(board, angle * Space.R - half - OldBoardW) : angle * Space.R - half - OldBoardW;
        // 板と窓が両方は入らなければ、板の側を見せる (窓の右端は本文が少ない)
        var right = Math.Min(angle * Space.R + half, left + 2 * (halfViewport - FollowMargin));
        viewTarget = Space.FollowX(viewTarget, (left + right) * 0.5f, (right - left) * 0.5f + FollowMargin, halfViewport);
    }

    // 視点が追うときに窓の外に残す幅 (メートル)
    const float FollowMargin = 0.03f;

    // 窓が光を落とす床の高さと、円筒の軸からの距離 (原点は Recenter した両眼の中点。床の高さは仮)
    const float FloorY = -1.1f;
    const float FloorR = Space.R * 0.8f;

    // 控えた frame の帯: ゲームの絵のウィンドウの一部で、止めている間だけ、絵の下の端に重ねて出す (絵は縮めない)。
    // 帯の高さ、止めた位置の印の幅と高さ、絵の端との間 (画面の座標)
    const float TimelineH = 4;
    const float TimelineMarkW = 3;
    const float TimelineMarkH = 14;
    const float TimelineMargin = 12;

    // 控えた frame の at 番目 (0 が直近) の、帯の上の位置 (帯の幅を 1 とし、真ん中が 0、右端が直近)
    static float TimelineX(int at, int count)
    {
        return 0.5f - (at + 0.5f) / count;
    }

    // 帯を出す矩形 (印の高さを含む。出さなければ null)。
    // 保存した版のゲームの絵の下の端に置く。範囲 (LIDER_AREA) を決めていれば、絵のうち範囲に入る所の下の端に置く
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

    // 止めている間、帯を出し、止めた位置の印を今の frame の所へ滑らせる (lider を隠している間も)
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

    // ゲームの絵のウィンドウの上に、帯と、止めた位置の印を描く (labelBatch の pass の中で呼ぶ)
    static void DrawTimeline()
    {
        var band = TimelineRect();
        if (band == null || band.W <= 0)
            return;
        var y = band.Y + band.H * 0.5f;
        labelBatch!.Rect(band.X, y - TimelineH * 0.5f, band.W, TimelineH, Color.Rgb(0.3f, 0.35f, 0.45f, 0.6f));
        // 光の点は frame を送るたびに次の frame の所へ滑る
        labelBatch.Rect(band.X + (0.5f + timeline.Value) * band.W - TimelineMarkW * 0.5f, band.Y, TimelineMarkW, TimelineMarkH, Color.Rgb(1.0f, 0.6f, 0.15f, 1));
    }

    // 頭の座標 (両眼の中点が原点、+Z が前) から lider の空間への行列
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

    // 頭の向き (両眼の中点から、左眼の向きの前方) を lider の空間の ray にする
    static Ray Gaze(XrView left, XrView right)
    {
        var o = left.Orientation;
        var dir = new Quat(o[0], o[1], o[2], o[3]).RotateVec3(new Vec3(0, 0, -1));
        var pos = new Vec3((left.Position[0] + right.Position[0]) * 0.5f, (left.Position[1] + right.Position[1]) * 0.5f,
            (left.Position[2] + right.Position[2]) * 0.5f);
        return FromLocal(pos, dir);
    }

    // 手 (0 左、1 右) の aim を lider の空間の ray にする (pose が取れなければ null)
    static Ray? HandRay(int hand)
    {
        var h = Xr.Input(hand);
        if (h == null || !h.PoseValid)
            return null;
        var o = h.AimOrientation;
        var dir = new Quat(o[0], o[1], o[2], o[3]).RotateVec3(new Vec3(0, 0, -1));
        return FromLocal(new Vec3(h.AimPosition[0], h.AimPosition[1], h.AimPosition[2]), dir);
    }

    // 手の grip の位置を lider の空間にする (pose が取れなければ null)
    static Vec3? GripPoint(int hand)
    {
        var h = Xr.Input(hand);
        if (h == null || !h.PoseValid)
            return null;
        return FromLocal(new Vec3(h.GripPosition[0], h.GripPosition[1], h.GripPosition[2]), new Vec3(0, 0, -1)).Origin;
    }

    // 両手の引き金: 指している所を押し、引いたまま動かすと引く。押して引けるのは片方の手ずつで、先に引いた手が離すまで続く。
    // oskOnly (focus がゲームにある間) は、画面のキーボードだけを押す (focus を戻す key を打てるように)
    static void Triggers(Workspace w, bool oskOnly)
    {
        for (var h = 1; h >= 0; h--)
        {
            var input = Xr.Input(h);
            var ray = HandRay(h);
            // 押し離しは閾値を分けて、ぶれで繰り返さない
            var down = input != null && ray != null && (triggerDown[h] ? input.Trigger > 0.3f : input.Trigger > 0.7f);
            if (down && !triggerDown[h] && oskOnly)
                PressOsk(w, ray!.Value);
            else if (down && !triggerDown[h] && activeHand < 0)
            {
                activeHand = h;
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

    // 手 hand の ray が指している面と位置を決める。
    // 手前のスクリーンから見て、ウィンドウの上に当たっていればそこ、どのウィンドウにも当たっていなければ、当たった一番奥の面
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

        // 左手の X でインスペクタを出し入れする
        var x = left != null && left.Primary;
        if (x && !inspectButton)
            inspect.Shown = !inspect.Shown;
        inspectButton = x;
        // 右の stick で、インスペクタを指していればそれを、でなければ focus のある窓を 1 秒に 6 notch まで scroll する
        if (right != null && Math.Abs(right.StickY) > 0.2f && InspectAt(aim) != null)
            inspect.Scroll -= right.StickY * dt * 18;
        else if (right != null && w.FocusWindow != null && Math.Abs(right.StickY) > 0.2f)
            EditorHost.Scroll(w.FocusWindow.Code.ViewId, right.StickY * dt * 6);
        // 止めている間、左の stick を倒すと frame を送る (倒したままなら 0.12 秒ごと)
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

    // 窓の見た目 (まだ描いていなければ null)
    public static WindowMotion? MotionOf(Window window)
    {
        return motions.TryGetValue(window, out var m) ? m : null;
    }

    // 窓の見た目を目標へ寄せ、保存・reload・打鍵・focus の移りに手応えを返す
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
                // 開いた窓は、その場で小さな大きさから広がる
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
                // タブを替えたら、その file の今の状態から追う (替えたことを保存や反映と取り違えない)
                m.SeenDoc = window.Code.Doc;
                m.SeenSaved = window.Code.Doc.Saved;
                m.SeenLanded = window.Code.Doc.Landed;
                m.SeenRevision = window.Code.Doc.Revision;
                m.SeenRemoved = EditorHost.RemovedCount(window.Code.ViewId);
            }
            // つかんでいる窓と、Alt で動かしている窓は手前へ出す
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
                // 保存した内容がゲームに入ると、窓からゲームの空間へ光が飛ぶ
                m.SeenLanded = window.Code.Doc.Landed;
                m.LandSeconds = now - m.SavedAt;
                m.LandLabel = 2.5f;
                Land(m, repeat, celebrate);
                celebrate = false;
                LastLandAt = now;
            }
            // 行が面に沈み終えると、そこから波が広がる
            var panel = Panel(window);
            if (panel.Step(dt) > 0)
            {
                m.Ripple = 1;
                m.RippleAt = panel.SankAt;
            }
            // 本文から文字が消えると、消えた所から粒が散る
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
                // 保存した版が compile error なら、error のある file の窓が赤く光り、赤い粒がこぼれる
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
        // 窓の無い file の error が直ったときは、focus のある窓で祝う
        if (celebrate && w.FocusWindow != null && motions.TryGetValue(w.FocusWindow, out var here))
            Land(here, false, true);
        // 閉じた窓はその場で縮んでから消す
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

    // 保存した内容がゲームに届いた: 窓の枠が揺れ、全面が明滅し、輪と破片が飛ぶ。
    // 続けて保存している間は短くする。error が直った時は、それより大きく祝う
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
        // 紙吹雪: 赤く震えていた所が通った、一番の報酬
        var colors = new List<Color> { Color.Rgb(1, 0.82f, 0.2f), Color.Rgb(0.4f, 1, 0.55f), Color.Rgb(0.45f, 0.8f, 1), Color.Rgb(1, 0.45f, 0.7f), Color.Rgb(1, 1, 1) };
        foreach (var c in colors)
            particles.Burst(m.Angle.Value, m.Y.Value + PanelView.BaseH * m.Scale.Value * 0.3f, r, 16, 2.4f, -0.8f, c, 1.1f, 3);
        rings.Add(new Ring { Angle = m.Angle.Value, Y = m.Y.Value, Size = PanelView.BaseH * m.Scale.Value * 0.5f, Color = Color.Rgb(1, 0.82f, 0.2f) });
    }

    // focus のある窓の cursor の見た目を、エディタが最後に描いた所へ滑らせる (focus が来たときは、その場に出す)。
    // 同じ行で cursor が進み、行がその分だけ長くなったら、進んだ所を打った文字として Landing にする
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

    // カーソルのある行の、円筒の上の高さ
    static float CursorY(Window window, WindowMotion m)
    {
        var id = window.Code.ViewId;
        var panel = Panel(window);
        var v = (EditorHost.LineY(id, EditorHost.CursorLine(id)) + (text != null ? text.LineH * 0.5f : 0)) / window.PxH;
        return m.Y.Value + (0.5f - v) * window.UnscaledSize.Y * m.Scale.Value;
    }

    // 窓の view の中の (x, y) px (行の左上) から粒を散らす (窓の外なら散らさない)
    static void BurstAt(Window window, WindowMotion m, float x, float y, Color color, int n, float life)
    {
        var panel = Panel(window);
        var lh = EditorHost.LineHeight(window.Code.ViewId);
        if (y < 0 || y + lh > window.PxH || x < 0 || x > window.PxW)
            return;
        particles.Burst(panel.AngleAt(m.Angle.Value, m.Scale.Value, x), panel.YAt(m.Y.Value, m.Scale.Value, y + lh * 0.5f),
            Space.R - PanelView.FrontGap - 0.01f, n, 0.25f, -0.3f, color, life);
    }

    // XR の LOCAL 空間の点と向きを、lider の空間の ray にする
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
        // 視点に固定したスクリーンは、頭の座標で描く
        viewVp = Space.FromXr(eyeVp) * headToWorld;
        // Space に置いたゲームが眼に描いていれば、その上に描き足す。置いていなければ、眼を塗り直してから描く
        Gfx.BeginPass(new PassOpts { Target = view.Target, Load = w.InSpace != null ? Gfx.LoadAction.Load : Gfx.LoadAction.Clear, ClearColor = new float[] { 0, 0, 0, 1 } });
        DrawScene(w, Space.FromXr(eyeVp));
        Gfx.EndPass();
    }

    // 輪は 4 本の棒で、広がりながら細くなる
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

    // 全面の明滅: 暗い色を一瞬混ぜてから明るくし、どんな背景でも見えるようにする
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

    // XR で focus のある窓が視野の外なら、その向きへ小さくなる点の列を、見ている所の横に出す
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

    // focus のある窓が外れたとみなす視野の半分の角度と、点の列を置く見ている所からの角度
    const float CueFov = 0.5f;
    const float CueOffset = 0.35f;

    static void DrawScene(Workspace w, Mat4 vp)
    {
        Quad3d.Opacity = Opacity;
        // 止めている間は、空間を冷たい色で包む
        if (cold > 0.001f)
            Quad3d.CurvedSolid(vp, Color.Rgb(0.25f, 0.45f, 1.0f, 0.07f * cold), 0, 0, Space.R + 2, 2 * (float)Math.PI * (Space.R + 2), 6);
        if (ZenShown)
            Quad3d.CurvedSolid(vp, Color.Rgb(0, 0, 0, 0.55f), 0, 0, Space.R + 2, 2 * (float)Math.PI * (Space.R + 2), 40);
        // 窓が足元に落とす光 (focus のある窓は少し明るい)
        foreach (var pair in motions)
        {
            // 床に光を落とすのは、世界に置いた窓だけ
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
        // XR では、平面のスクリーンの上の絵 (ゲームの絵、インスペクタ) を、スクリーンの面として、ほかの物より先に描く (XR でなければ ScreenView が画面に写す)
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
                    // 画面の上の矩形のうち中身を映す所を、スクリーンの面の上の矩形に直す
                    var perM = whole.W / window.Size.X;
                    var at = window.Position.Value;
                    var rect = new Vec4(at.X + (part.X + part.W * 0.5f - whole.X - whole.W * 0.5f) / perM,
                        at.Y - (part.Y + part.H * 0.5f - whole.Y - whole.H * 0.5f) / perM, part.W / perM, part.H / perM);
                    SurfaceQuad(screen, worldVp, rect, Color.Rgb(1, 1, 1, screen.Opacity), picture);
                }
            }
        foreach (var pair in motions)
        {
            var window = pair.Key;
            var m = pair.Value;
            var angle = m.Angle.Value;
            var y = m.Y.Value;
            var scale = m.Scale.Value;
            var panel = Panel(window);
            // 視点に固定した平面のスクリーンの上の窓は、視点の座標で、曲げずに描く
            var onView = OnPlane(ScreenFor(w, window));
            vp = VpOf(ScreenFor(w, window), worldVp);
            Space.Flat = worldFlat || onView;
            panel.Flat = lastEye == null;
            panel.FrameAngle = (particles.Next() - 0.5f) * 0.03f * m.Shake * m.Shake;
            panel.FrameY = (particles.Next() - 0.5f) * 0.05f * m.Shake * m.Shake;
            panel.Draw(window.Code.Doc, angle, y, scale, vp, m.Z.Value, m.Glow.Value, m.Alarm);
            // 動いている版の板は、コードのウィンドウの子: 親の座標で置き、親より手前に描く
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
                // 2 つ目からの cursor (複数の選択) は滑らせない
                for (var i = 1; i < EditorHost.CaretCount(id); i++)
                    panel.DrawCaret(vp, window.Code.Doc, angle, y, scale, m.Z.Value, EditorHost.CaretX(id, i), EditorHost.CaretY(id, i));
            }
            var r = Space.R + m.Z.Value - PanelView.FrontGap - 0.005f;
            var width = window.UnscaledSize.X * scale * r / Space.R;
            // 保存の波: 明るい帯が上から下へ流れる
            if (m.Wave > 0)
                Quad3d.CurvedSolid(vp, Color.Rgb(0.6f, 0.95f, 1.0f, 0.45f * m.Wave), angle,
                    y + window.UnscaledSize.Y * scale * (m.Wave - 0.5f), r, width, 0.025f * scale);
            // 沈み終えた行から上と下へ広がる波
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
            // compile error の赤い光
            if (m.Alarm > 0)
                Quad3d.CurvedSolid(vp, Color.Rgb(1.0f, 0.2f, 0.15f, 0.25f * m.Alarm), angle, y, r, width,
                    window.UnscaledSize.Y * scale);
            // 飛んだ先の行の印: 明るい帯を暗い線で挟む (背景によらず見える)
            if (m.Arrive > 0 && text != null && window == w.FocusWindow)
            {
                var cy = CursorY(window, m);
                var bandH = text.LineH / window.PxH * window.UnscaledSize.Y * scale;
                Quad3d.CurvedSolid(vp, Color.Rgb(0.5f, 0.9f, 1.0f, 0.5f * m.Arrive), angle, cy, r, width, bandH);
                Quad3d.CurvedSolid(vp, Color.Rgb(0, 0, 0, 0.7f * m.Arrive), angle, cy + bandH * 0.55f, r, width, bandH * 0.1f);
                Quad3d.CurvedSolid(vp, Color.Rgb(0, 0, 0, 0.7f * m.Arrive), angle, cy - bandH * 0.55f, r, width, bandH * 0.1f);
            }
            // 打った行が一瞬光る
            if (m.Pulse > 0 && text != null && window == w.FocusWindow)
                Quad3d.CurvedSolid(vp, Color.Rgb(1.0f, 0.85f, 0.4f, 0.18f * m.Pulse), angle,
                    CursorY(window, m), r, width, text.LineH / window.PxH * window.UnscaledSize.Y * scale);
        }
        Space.Flat = worldFlat;
        // 画面に貼った平面でないスクリーンの上のゲームの絵は、空間の面の上に描く (画面に貼った平面の上の絵は ScreenView が描く)
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
            }
        }
        // 窓に付く光と印は、コードのスクリーンと一緒に動く
        vp = VpOf(w.CodeScreen, worldVp);
        // focus が移ると、前の窓から次の窓へ光が流れる
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
        if (TreeVisible && inspect.Texture != null)
        {
            var onView = OnPlane(ScreenFor(w, inspectWindow));
            var size = InspectSize();
            var on = VpOf(ScreenFor(w, inspectWindow), worldVp);
            Space.Flat = worldFlat || onView;
            Theme.Plate(on, Theme.Surface, BoardAngle, InspectY(), Space.R, size.X, size.Y);
            Quad3d.Curved(on, inspect.Texture, Color.Rgb(1, 1, 1, 1), BoardAngle, InspectY(), Space.R, size.X, size.Y, 0, 1);
            Theme.Plate(on, Theme.Surface, BoardAngle, InspectTitleY(), Space.R, size.X, InspectTitleH);
            if (inspectTitle.Texture != null)
                Quad3d.Curved(on, inspectTitle.Texture, Color.Rgb(1, 1, 1, 1), BoardAngle, InspectTitleY(), Space.R, size.X, InspectTitleH, 0, 1);
            Space.Flat = worldFlat;
        }
        // コントローラーで指している点 (両手。指している面の上に、見かけが同じ大きさで出す)
        for (var h = 0; lastEye != null && h < 2; h++)
        {
            var on = aimScreen[h];
            if (on == null)
                continue;
            var dot = 0.014f / Apparent(on);
            SurfaceQuad(on, worldVp, new Vec4(aimAt[h].X, aimAt[h].Y, dot, dot), Color.Rgb(0.4f, 0.9f, 1.0f, 0.95f), null);
        }
        foreach (var ring in rings)
            DrawRing(vp, ring);
        DrawFlash(vp);
        DrawFocusCue(w, vp);
        DrawEdge(w, vp);
        // 画面のキーボードは、ほかのウィンドウより手前に描く
        var keys = w.ScreenOf(oskWindow);
        if (keys != null && keys.Shown && oskWindow.Position != null && osk.Picture != null)
            SurfaceQuad(keys, worldVp, new Vec4(oskWindow.Position.Value.X, oskWindow.Position.Value.Y, oskWindow.Size.X, oskWindow.Size.Y), Color.Rgb(1, 1, 1, 1), osk.Picture);
        DrawScreenTags(w, worldVp);
        // 案内は、平面のスクリーンの上に、曲げずに描く
        Space.Flat = true;
        hintCard.Draw(VpOf(hud, worldVp));
        Space.Flat = worldFlat;
        // 検索とアセットも、平面のスクリーンの上に、曲げずに描く
        Space.Flat = true;
        if (assetWindow.Position != null)
            AssetBoard.Draw(VpOf(palette, worldVp), WindowAngle(w, assetWindow), WindowTop(w, assetWindow), SearchScale);
        if (searchWindow.Position != null)
            searchView.Draw(VpOf(palette, worldVp), WindowAngle(w, searchWindow), WindowTop(w, searchWindow), SearchScale);
        Space.Flat = worldFlat;
        Quad3d.Opacity = 1;
    }
}
