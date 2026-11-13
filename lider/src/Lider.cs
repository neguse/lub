// lider の entry。
// `lub Lider.csproj` で起動し、LIDER_GAME のゲームを別の VM で開いて動かす (GameApp)。ゲームの絵を画面に写し、その上にコードの空間を描く。
// F1 で lider がキーボードとマウスを取り、コードの空間を出す (編集)。もう一度 F1 で入力をゲームに返し、窓は同じ所に薄く残す (試す)。
// Shift+F1 で窓も消してゲームだけにする。起動したときはゲームだけ。
// 左 click は窓ならカーソルを置き、何も無い所ならゲームの物を描いたコードを開く。中 click は、前にコードがあってもゲームの物を指す。
// 窓は横一列に並ぶ。非 XR の右ドラッグは、窓の上なら並びを変え (ドラッグ中のホイールで大きさを変える)、窓の外なら列を動かす。
// Alt + 左右で隣の窓へ、Alt + Shift + 左右で並びを変え、Alt + 数字でその番目の窓へ移る。非 XR では focus のある窓が見える所まで視点が滑る。
// env:
//   LIDER_GAME    開くゲーム。.csproj か、ディレクトリ (その下の .cs。web) なら lider が変換し (GameBuild)、.tcsb ならそのまま開く
//   LIDER_CS_LIB  LIDER_GAME がディレクトリのときに一緒に変換する lub の cs-lib のディレクトリ
//   LIDER_ENTRY   LIDER_GAME がディレクトリのときの entry class (無ければ OnFrame を持つ static class)
//   LIDER_ROOT    並べる .cs を探すディレクトリ (既定はゲームの .csproj のディレクトリ、LIDER_GAME が無ければ ".")
//   LIDER_SIZE    画面の大きさ ("幅x高さ")
//   LIDER_HOME    lider のディレクトリ (font を data/ から読む。既定 ".")
//   LIDER_ACTIVE  1 なら起動したときから入力を取る
//   LIDER_OPEN    起動したときに拡大する file の path
//   LIDER_REWIND  控える frame の数 (既定 600、0 で控えない)
// 時間: F5 でゲームを止める / 続ける、止めている間 F6 で 1 frame 戻り、F7 で 1 frame 進む。Shift+F5 でゲームを最初から作り直す。
// F8 で compile error の行へ順に飛ぶ。
// F10 で、保存した版と編集中の版 (保存していない本文で変換した版) のゲームを左右に並べる。もう一度 F10 で戻す。
// 並べている間、入力は両方のゲームに渡し、F3 で保存した版の今の状態を編集中の版に写す (DraftGame)。
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
    // インスペクタの板の真ん中の高さ。範囲を決めていれば、窓の見出しも入れた高さの真ん中に揃える
    static float InspectY => AreaW > 0 ? PanelView.TitleH * Space.FlatScale * 0.5f : 0;
    static float followedAngle;
    static float followedScale;
    static float followedBoard;
    static float followedOld;
    // 非 VR の縦の画角 (度)。
    // ホイールで狭めると拡大して読める
    static float fov = Space.FlatFov;
    static Mat4 lastVp = new Mat4();
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
    static bool triggerDown;
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
    // 音と演出の強さ (F4 で 2 全部、1 音なし、0 演出も抑える)
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
    // 止めている間の空間の冷え (0 から 1) と、足元の帯の上の点の位置
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
    // インスペクタの板 (F2 か左手の X で出し入れ)。focus のある窓の左に、窓より手前に置く
    static readonly InspectView inspect = new InspectView();
    public static InspectView Inspector => inspect;
    static float inspectAngle = -0.6f;
    static bool TreeVisible => inspect.Shown && !ZenShown;
    // 窓を画面の幅に広げ、左の板を隠し、ゲームを暗くする。試す間は効かせない (ゲームを遊べるように)
    public static bool Zen;
    static bool ZenShown => Zen && Work != null && Work.Active;
    // 窓を描かず、ゲームだけを見せる
    public static bool GameOnly = true;
    // 空間に置いたスクリーン (奥のゲームの絵と、コードの窓の円筒)。
    // 視点に固定した平面のスクリーンは ScreenView が先に描き、コードの窓と板は、その手前に DrawScene が描く
    public static readonly List<Screen> Screens = new List<Screen>();
    // 一番奥のスクリーン: 視野をちょうど覆い、ゲームの絵を映す
    static Screen backdrop = new Screen("backdrop");
    // 編集中の版のゲームを映すウィンドウ (並べている間だけ)
    static Window? draftWindow;
    const float BackdropDistance = 10;
    // lider の描く物の濃さ (編集は 1、試す間は TryOpacity へ寄せる)
    public static float Opacity = 1;
    const float TryOpacity = 0.35f;
    static bool inspectButton;
    const float InspectR = Space.R - 0.12f;
    // インスペクタの板と窓の間 (メートル)
    const float InspectGap = 0.05f;
    // 非 XR は窓を大きく開くので、インスペクタの字も同じくらいにする
    static float InspectScale => Space.Flat ? 1.4f : 1;
    // 指している物に対して、そのとき効く操作の案内
    static readonly HintCard hintCard = new HintCard();
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
    static float searchAngle;
    static float searchTop;

    public static void OnInit()
    {
        var size = (Environment.GetEnvironmentVariable("LIDER_SIZE") ?? "").Split('x');
        if (size.Length == 2 && int.TryParse(size[0], out var width) && int.TryParse(size[1], out var height))
            App.Config(new ConfigOpts { Width = width, Height = height });
        GameApp.Open(Environment.GetEnvironmentVariable("LIDER_GAME") ?? "");
        backdrop = new Screen("backdrop") { Position = new Vec3(0, 0, BackdropDistance) };
        draftWindow = null;
        DraftGame.Close();
        backdrop.Windows.Add(new Window(new GameView()));
        Screens.Clear();
        Screens.Add(backdrop);
        var area = (Environment.GetEnvironmentVariable("LIDER_AREA") ?? "").Split(',');
        if (area.Length == 4 && float.TryParse(area[0], out var ax) && float.TryParse(area[1], out var ay)
            && float.TryParse(area[2], out var aw) && float.TryParse(area[3], out var ah))
            SetArea(ax, ay, aw, ah);
        // 本文と動いている版の違いを、最初から窓の中 (インライン) に出す (F9 で切り替えられる)
        if (Environment.GetEnvironmentVariable("LIDER_DIFF") == "inline")
            PanelView.SideBySide = false;
        // 止める前の frame へ戻れるよう、lider を出していなくても起動したときから控える。
        // 控えている間、ゲームを進める前ごとにゲームの状態 (static と、そこから辿れる object) を全部写すので、その分だけ毎 frame 重い。
        // LIDER_REWIND=0 で控えない
        Record(Environment.GetEnvironmentVariable("LIDER_REWIND"));
        Work = new Workspace(Environment.GetEnvironmentVariable("LIDER_ROOT") ?? GameDir());
        Screens.Add(Work.CodeScreen);
        if (Environment.GetEnvironmentVariable("LIDER_ACTIVE") == "1")
            SetActive(true);
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

    public static void SetActive(bool active)
    {
        if (Work == null)
            return;
        Work.Active = active;
        if (active)
            GameOnly = false;
        else if (Search.Shown)
            Search.Cancel(Work);
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
        if (e.Kind == EventKind.KeyDown && e.Key == Keys.F1 && w.Shift)
        {
            if (!repeated)
            {
                SetActive(false);
                GameOnly = !GameOnly;
            }
            return;
        }
        if (e.Kind == EventKind.KeyDown && e.Key == Keys.F1)
        {
            if (!repeated)
                SetActive(!w.Active);
            return;
        }
        // 試す間とゲームだけの間は、入力はゲームのもの。編集の間は、キーボードとマウスの他だけをゲームに渡す
        if (!w.Active)
        {
            SendGame(e);
            return;
        }
        if (e.Kind == EventKind.WindowResize || e.Kind == EventKind.Other)
            SendGame(e);
        if (Search.Shown && SearchEvent(w, e))
            return;
        if (e.Kind == EventKind.KeyDown && w.Ctrl && !w.Shift && e.Key == Keys.P && !EditorHost.Modal())
        {
            Search.Open(w);
            if (lastEye != null)
                PlaceSearch(gaze);
            return;
        }
        if (e.Kind == EventKind.KeyDown && e.Key == Keys.F12 && !EditorHost.Modal())
        {
            Search.Jump(w);
            if (lastEye != null)
                PlaceSearch(gaze);
            return;
        }
        if (e.Kind == EventKind.KeyDown && w.Ctrl && e.Key == Keys.Minus && !EditorHost.Modal())
        {
            w.Back();
            return;
        }
        if (e.Kind == EventKind.KeyDown && e.Key == Keys.F8 && !EditorHost.Modal())
        {
            var error = w.NextError();
            if (error != null)
                Search.GoToPlace(w, error.Path, error.Line);
            return;
        }
        if (e.Kind == EventKind.KeyDown && w.Ctrl && TabKey(w, e.Key))
            return;
        // Alt を押している間、左右と数字で focus を移し、Shift も押すと並びを変え、PageUp / PageDown で大きさを変え、Delete で file を消す
        if (e.Kind == EventKind.KeyDown && w.Alt && WindowKey(w, e.Key))
            return;
        if (e.Kind == EventKind.KeyDown && e.Key == Keys.F11)
        {
            if (!repeated)
                Zen = !Zen;
        }
        else if (e.Kind == EventKind.KeyDown && e.Key == Keys.F2)
        {
            // Shift+F2 は板を値の木と負荷の一覧で切り替える (隠していれば負荷の一覧で出す)
            if (!repeated && w.Shift)
            {
                inspect.Load = !inspect.Shown || !inspect.Load;
                inspect.Shown = true;
            }
            else if (!repeated)
                inspect.Shown = !inspect.Shown;
        }
        else if (e.Kind == EventKind.KeyDown && e.Key == Keys.F10)
        {
            if (!repeated)
                ToggleCompare();
        }
        else if (e.Kind == EventKind.KeyDown && e.Key == Keys.F3)
        {
            if (!repeated)
                DraftGame.CopyPending = true;
        }
        else if (e.Kind == EventKind.KeyDown && e.Key == Keys.F9 && !Composing())
        {
            if (!repeated)
                PanelView.SideBySide = !PanelView.SideBySide;
        }
        else if (e.Kind == EventKind.KeyDown && e.Key == Keys.F4)
        {
            if (!repeated)
                SetJuice((Juice + 2) % 3);
        }
        else if (e.Kind == EventKind.KeyDown && e.Key == Keys.F5)
        {
            // Shift+F5 はゲームを最初から作り直す (次の frame の始めに OnInit から始め直す。lider は作り直されない)
            if (!repeated && w.Shift)
                GameApp.Restart();
            else if (!repeated)
                TogglePause();
        }
        // かなの変換中の F6 と F7 は、ひらがなとカタカナにする (Workspace.Key から IME へ)
        else if (e.Kind == EventKind.KeyDown && e.Key == Keys.F6 && !Composing())
            StepBack();
        else if (e.Kind == EventKind.KeyDown && e.Key == Keys.F7 && !Composing())
            StepForward();
        else if (e.Kind == EventKind.KeyDown || e.Kind == EventKind.KeyUp)
            w.Input(e.Key, e.Kind == EventKind.KeyDown, e.Typed, e.Plain);
        else if (e.Kind == EventKind.MouseButtonDown && e.Button == 1)
            ClickAt(e.X, e.Y);
        else if (e.Kind == EventKind.MouseButtonDown && e.Button == 3 && lastEye == null)
            GrabAt(w, e.X, e.Y);
        else if (e.Kind == EventKind.MouseMotion && grab.Holding && lastEye == null)
        {
            var at = Space.OnCylinderAt(PointerRay(e.X, e.Y), Space.R);
            if (at != null)
                grab.Move(w.CodeScreen.XAt(at.Angle), at.Y);
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
            var onBoard = lastEye == null && searchView.Hit(PointerRay(e.X, e.Y), searchAngle, searchTop, SearchScale);
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

    // ray の指す所に検索の板を置く
    static void PlaceSearch(Ray ray)
    {
        var at = Space.OnCylinderAt(ray, Space.R);
        if (at == null)
            return;
        searchAngle = at.Angle;
        searchTop = at.Y;
    }

    // 指している窓をマウスでつかむ
    static void GrabAt(Workspace w, float x, float y)
    {
        var ray = PointerRay(x, y);
        var hit = TitleAt(w, ray) ?? WindowAt(w, ray);
        var at = Space.OnCylinderAt(ray, Space.R);
        if (hit != null && at != null)
            grab.Start(hit.Window, w.CodeScreen.XAt(at.Angle), at.Y);
    }

    const int TabScancode = 43;
    const int WScancode = 26;

    // Ctrl を押しているときのタブの key: Ctrl+Tab / Ctrl+Shift+Tab で隣のタブ、Ctrl+W で閉じる、Ctrl+Alt+←→ で隣の窓へ移す。
    // 扱った key なら true
    static bool TabKey(Workspace w, int key)
    {
        if (EditorHost.Modal() || Composing())
            return false;
        if (key == TabScancode)
            w.NextTab(w.Shift ? -1 : 1);
        else if (key == WScancode && !w.Alt && !w.Shift)
            w.CloseTab();
        else if (w.Alt && (key == Keys.Left || key == Keys.Right))
            w.MoveTab(key == Keys.Left ? -1 : 1);
        else
            return false;
        return true;
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

    // Alt を押しているときの key。
    // 扱った key なら true
    public static bool WindowKey(Workspace w, int key)
    {
        if (key >= Keys.Digit1 && key < Keys.Digit1 + 9)
        {
            w.FocusNth(key - Keys.Digit1);
            return true;
        }
        var window = w.FocusWindow;
        if (window == null)
            return false;
        if (key == Keys.Delete)
        {
            w.AskDeleteFocus();
            return true;
        }
        if ((key == Keys.Left || key == Keys.Right) && !w.Shift)
        {
            w.FocusBy(key == Keys.Left ? -1 : 1);
            return true;
        }
        if (key == Keys.Left)
            w.MoveFocus(-1);
        else if (key == Keys.Right)
            w.MoveFocus(1);
        else if (key == Keys.PageUp && w.Shift)
            window.Resize(window.WidthPx + WidthStep, window.HeightPx);
        else if (key == Keys.PageDown && w.Shift)
            window.Resize(window.WidthPx - WidthStep, window.HeightPx);
        else if (key == Keys.PageUp)
            window.Scale = Math.Min(Grab.MaxScale, window.Scale * 1.1f);
        else if (key == Keys.PageDown)
            window.Scale = Math.Max(Grab.MinScale, window.Scale / 1.1f);
        else
            return false;
        // 動かしている間は、つかんだときと同じに浮かせる (key の繰り返しが続く間)
        keyMoved = window;
        keyLift = 0.3f;
        return true;
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
        var focus = w.Active && !Search.Shown && w.Focus != null ? w.Focus.Path : "";
        if (!caretLog || focus == loggedFocus)
            return;
        loggedFocus = focus;
        Console.WriteLine("lider_focus " + focus);
    }

    // インスペクタの板の、最後に描いた frame の画面の px (左, 上, 右, 下)
    public static Vec4 BoardViewport()
    {
        var w = InspectView.W * InspectScale * 0.5f;
        var h = InspectView.H * InspectScale * 0.5f;
        var a = ViewportPoint(Space.At(inspectAngle - w / InspectR, InspectY + h, InspectR));
        var b = ViewportPoint(Space.At(inspectAngle + w / InspectR, InspectY - h, InspectR));
        return new Vec4(a.X, a.Y, b.X, b.Y);
    }

    // 足元の帯 (止めた位置の印の高さを含む) の、最後に描いた frame の画面の px (左, 上, 右, 下)
    public static Vec4 TimelineViewport()
    {
        var half = timelineW * 0.5f / timelineR;
        var h = TimelineMarkH * timelineR / Space.R * 0.5f;
        var a = ViewportPoint(Space.At(timelineAngle - half, timelineY + h, timelineR));
        var b = ViewportPoint(Space.At(timelineAngle + half, timelineY - h, timelineR));
        return new Vec4(a.X, a.Y, b.X, b.Y);
    }

    // 空間の位置の、最後に描いた frame の画面の px (左上が 0,0)
    public static Vec2 ViewportPoint(Vec3 p)
    {
        return ViewportOf(lastVp, p);
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
        pickX = x;
        pickY = y;
        Click(w, lastEye != null ? gaze : PointerRay(x, y), true);
    }

    // コードの板が前にあっても、その奥のゲームの物を描いたコードを探す (中ボタン)
    public static void PickAt(float x, float y)
    {
        var w = Work;
        if (w == null)
            return;
        pickX = x;
        pickY = y;
        RequestPick(lastEye != null ? gaze : PointerRay(x, y));
    }

    // 指した所で次のどれかをする: 窓の閉じる印なら閉じる、窓の見出しならその窓に focus を移す、窓ならその窓に focus を移してカーソルを動かす、どれでもなければゲームの物を描いたコードを探す (Gfx.Pick)
    public static void Click(Workspace w, Ray ray, bool pickGame)
    {
        var uv = InspectAt(ray);
        if (uv != null && inspect.Load)
        {
            var tab = inspect.TabAt(uv.Value);
            if (tab >= 0)
                Costs.Metric = tab;
            var load = inspect.LoadAt(uv.Value);
            if (load != null)
                Search.GoToPlace(w, load.Path, load.Line - 1);
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
            w.FocusWindow = edged;
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
                w.FocusWindow = head.Window;
            return;
        }
        var hit = WindowAt(w, ray);
        if (hit != null && text != null)
        {
            w.FocusWindow = hit.Window;
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
        if (pickGame)
            RequestPick(ray);
    }

    // ray が指している物に対する操作の案内を、指している点 at の右下に置く (画面への pass の外で呼ぶ)
    // LIDER_HINTS=0 なら、指している所に操作の案内を出さない
    static readonly bool hintsOff = Environment.GetEnvironmentVariable("LIDER_HINTS") == "0";

    static void ShowHints(Workspace w, Ray ray, CylinderPoint? at, float dt)
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
                PlaceHints(at, dt);
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
        foreach (var h in Hints.For(c))
            hints.Add(h);
        hintCard.Prepare(hints, text);
        PlaceHints(at, dt);
    }

    static void PlaceHints(CylinderPoint? at, float dt)
    {
        // 非 XR は画面の中に収める
        var halfH = Space.R * (float)Math.Tan(fov * 0.5f * Math.PI / 180);
        hintCard.Place(at, dt, Space.Flat ? -halfH : -1000, Space.Flat ? (viewX.Value + halfH * viewportW / viewportH) / Space.R : 1000);
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

    // 試す間はマウスとコントローラーがゲームのものなので、指している物の案内と光を消す
    static void HideHints()
    {
        hintCard.Place(null, 0, 0, 0);
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

    // 窓の左の板の幅 (メートル。出していなければ 0)
    static float BoardW => TreeVisible ? InspectView.W * InspectScale : 0;

    // 窓の左の板の中心の角度 (インスペクタの所を押すテストが使う)
    public static float BoardAngle => inspectAngle;

    // インスペクタの板を指していれば、板の中の位置
    static Vec2? InspectAt(Ray ray)
    {
        return TreeVisible ? Space.HitCurved(ray, inspectAngle, InspectY, InspectR, InspectView.W * InspectScale, InspectView.H * InspectScale) : null;
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
        // XR ではゲームを空間に描いていないので、指せない
        if (lastEye == null)
            GameApp.Pick(pickX, pickY);
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

    // コードの窓の、円筒の角度 (まだ並べていなければ並べる)
    public static float WindowAngle(Workspace w, Window window)
    {
        if (window.Bounds == null)
            w.Arrange(grab.Window);
        return window.Bounds != null ? w.CodeScreen.AngleAt(window.Bounds.X) : 0;
    }

    // コードの窓の真ん中の高さ (並べた窓は 0。つかんでいる間だけ動く)
    public static float WindowY(Workspace w, Window window)
    {
        if (window.Bounds == null)
            w.Arrange(grab.Window);
        return window.Bounds != null ? window.Bounds.Y : 0;
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
        return Space.HitCurved(ray, m != null ? m.Angle.Value : WindowAngle(w, window), m != null ? m.Y.Value : WindowY(w, window), Space.R, window.W * scale, window.H * scale);
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
        return Space.HitCurved(ray, m != null ? m.Angle.Value : WindowAngle(w, window), Panel(window).TitleY(m != null ? m.Y.Value : WindowY(w, window), scale), Space.R,
            window.W * scale, PanelView.TitleH * scale);
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
        var game = InspectAt(ray) == null && TitleAt(w, ray) == null && WindowAt(w, ray) == null && !Search.Shown;
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

    public static void OnFrame(float dt)
    {
        Gfx.Size(out var sw, out var sh);
        // スクリーンは、拡大 (fov) に付いていかない既定の画角で置く
        backdrop.Fill(Space.FlatFov, (float)sw / sh);
        foreach (var screen in Screens)
            if (screen.Anchor == ScreenAnchor.View && screen.Shape == ScreenShape.Plane)
                screen.Project(sw, sh, Space.FlatFov);
        var saved = GameWindow(GameVersion.Saved);
        var draft = GameWindow(GameVersion.Draft);
        // 入力は、マウスのあるゲームのウィンドウの中の位置で、どちらのゲームにも渡す
        Input.MousePos(out var mx, out var my);
        var from = draft != null && draft.ViewportRect!.Contains(mx, my) ? draft : saved;
        var grabbed = Work != null && Work.Active;
        if (saved != null)
            GameApp.Step(dt, grabbed, saved.ViewportRect!, from!.ViewportRect!, PixelRatio);
        if (draft != null && Work != null)
            DraftGame.Step(dt, grabbed, GameApp.Position >= 0, draft.ViewportRect!, from!.ViewportRect!, PixelRatio, Work.Docs);
        ScreenView.Draw(Screens, sw, sh);
        if (draft != null)
            DrawLabels(sw, sh, saved, draft);
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
        foreach (var screen in Screens)
            foreach (var window in screen.Windows)
                if (window.Content is GameView view && view.Version == version && window.ViewportRect != null)
                    return window;
        return null;
    }

    // 並べたゲームのウィンドウの左上に、どちらの版かの札を出す (lider を出していない間も)
    static MonoText? labelText;
    static SpriteBatch? labelBatch;
    const float LabelMargin = 8;
    const float LabelPad = 4;

    static void DrawLabels(int sw, int sh, Window? saved, Window draft)
    {
        var density = Math.Max(1, (int)Math.Round(PixelRatio));
        if (labelText == null || labelText.Density != density)
            labelText = new MonoText("lider_label_" + density, Ttf(), 15, density);
        if (labelBatch == null || labelBatch.LogicalW != sw || labelBatch.LogicalH != sh)
            labelBatch = new SpriteBatch("lider_label", sw, sh);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        labelBatch.Begin();
        if (saved != null)
            Label(saved.ViewportRect!, "保存済み");
        Label(draft.ViewportRect!, DraftGame.Label());
        labelBatch.Flush();
        Gfx.EndPass();
    }

    static void Label(PixelRect at, string text)
    {
        var t = labelText!;
        var w = (Utf8.Cells(text, Utf8.Bytes(text)) + 2) * t.CellW;
        var h = t.LineH + LabelPad * 2;
        var x = at.X + LabelMargin;
        var y = at.Y + LabelMargin;
        labelBatch!.Rect(x, y, w, h, Theme.Surface);
        t.Draw(labelBatch, text, x + t.CellW, y + LabelPad, Theme.Ink);
    }

    // ゲームの OnEvent に event を渡す (並べている間は、編集中の版にも)
    static void SendGame(EventData e)
    {
        GameApp.Send(e);
        DraftGame.Send(e);
    }

    // 保存した版と編集中の版を並べる / 戻す (背景のスクリーンを左右に等分する)
    public static void ToggleCompare()
    {
        if (DraftGame.Shown)
        {
            DraftGame.Close();
            if (draftWindow != null)
                backdrop.Windows.Remove(draftWindow);
            draftWindow = null;
            backdrop.Layout = new Full();
            return;
        }
        DraftGame.Open(GameApp.Project);
        draftWindow = new Window(new GameView(GameVersion.Draft));
        backdrop.Windows.Add(draftWindow);
        backdrop.Layout = new Even();
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
                    var half = Space.R * (float)Math.Tan(fov * 0.5f * Math.PI / 180);
                    var perPx = PanelView.BaseW / PanelView.BasePxW * window.Scale;
                    window.PxW = Math.Max(window.PxW, (int)(2 * half * sw / sh * ZenFill / perPx));
                    window.PxH = Math.Max(window.PxH, (int)(2 * half * ZenFill / perPx) - PanelView.TitlePx);
                }
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
        var bw = window.W * m.Scale.Value;
        var bh = window.H * m.Scale.Value;
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
        if (grab.Holding)
            return 0;
        var m = MotionOf(window);
        var scale = m != null ? m.Scale.Value : window.Scale;
        var angle = m != null ? m.Angle.Value : WindowAngle(w, window);
        var y = m != null ? m.Y.Value : WindowY(w, window);
        var bw = window.W * scale;
        var bh = window.H * scale;
        var uv = Space.HitCurved(ray, angle + EdgeW * 0.5f / Space.R, y - EdgeW * 0.5f, Space.R, bw + EdgeW, bh + EdgeW);
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
        var at = Space.OnCylinderAt(ray, Space.R);
        if (at == null)
            return;
        var perPx = PanelView.BaseW / PanelView.BasePxW * window.Scale;
        var pxW = window.WidthPx;
        var pxH = window.HeightPx;
        if ((sizingEdge & 1) != 0)
        {
            // 先頭の窓は真ん中が動かないので、端までの差の 2 倍だけ広げる
            var right = WindowAngle(w, window) * Space.R + window.W * window.Scale * 0.5f;
            var factor = w.Windows.IndexOf(window) == 0 ? 2 : 1;
            pxW += (int)((at.Angle * Space.R - right) * factor / perPx);
        }
        if ((sizingEdge & 2) != 0)
        {
            var bottom = WindowY(w, window) - window.H * window.Scale * 0.5f;
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
        if (GameOnly || !Quad3d.Ensure())
            return;
        PanelView.DeclareShaders();
        Opacity = Ease.Toward(Opacity, w.Active ? 1 : TryOpacity, dt, 0.25f);
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
            panel.Focused = window == w.FocusWindow;
            panel.Costs = Costs;
            panel.Fields = Fields.For(Search.DeclarationsIn(window.Code.Doc.Path), EditorHost.FirstLine(window.Code.ViewId), EditorHost.LastLine(window.Code.ViewId));
            panel.CloseLit = window == closeHover;
            var differs = window == w.FocusWindow && window.Code.Doc.Differs();
            panel.OldShown = differs && PanelView.SideBySide && !ZenShown;
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
        if (Search.Shown)
            searchView.Prepare(Search, panelText);
        AssetBoard.Prepare(w.ShownAsset, w.ShownAsset == "" ? "" : w.Relative(w.ShownAsset), panelText);
        if (w.FocusWindow != null)
        {
            var m = MotionOf(w.FocusWindow);
            var scale = m != null ? m.Scale.Value : w.FocusWindow.Scale;
            inspectAngle = (m != null ? m.Angle.Value : WindowAngle(w, w.FocusWindow))
                - (w.FocusWindow.W * scale * 0.5f + InspectGap + OldBoardW) / Space.R - BoardW * 0.5f / InspectR;
        }
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
            var aim = HandRay(1) ?? gaze;
            if (w.Active)
            {
                if (!Search.Shown)
                    w.Look(WindowAt(w, gaze)?.Window, dt);
                Controllers(w, aim, dt);
                ShowHints(w, aim, pointer, dt);
            }
            else
                HideHints();
            DrawEye(w, left);
            DrawEye(w, right);
            return;
        }
        lastEye = null;

        Gfx.Size(out var sw, out var sh);
        viewportW = sw;
        viewportH = sh;
        var halfViewportH = Space.R * (float)Math.Tan(fov * 0.5f * Math.PI / 180);
        if (w.Active && Input.MouseDown(3) && !grab.Holding)
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
        slide.M02 = -viewX.Value / Space.R;
        if (AreaW > 0)
        {
            // 窓の見出しは本文の上に乗るので、見出しも入れた高さの真ん中 (面の上の高さ TitleH / 2) を範囲の真ん中に合わせる
            slide.M12 = ((viewportH * 0.5f - AreaY - AreaH * 0.5f) / pxPerM - InspectY) / Space.R;
        }
        PlaceTimeline(pxPerM, slide.M12);
        lastVp = Camera3d.Vp(new Camera3dOpts
        {
            Eye = new Vec3(0, 0, 0),
            Target = new Vec3(0, 0, 1),
            Fov = fov,
            Near = 0.05f,
            Far = 100,
            Aspect = (float)viewportW / viewportH,
        }) * slide;
        Input.MousePos(out var mx, out var my);
        if (Search.Shown)
            PlaceSearch(PointerRay(viewportW * 0.5f, viewportH * 0.12f));
        if (w.Active)
        {
            Hover(w, PointerRay(mx, my), mx, my, dt);
            ShowHints(w, PointerRay(mx, my), Space.OnCylinderAt(PointerRay(mx, my), Space.R), dt);
        }
        else
            HideHints();
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
        var near = InspectR / Space.R / InspectScale;
        InspectView.SetSize(AreaW * AreaBoard / pxPerM * near, AreaH / pxPerM * near);
        var gap = InspectGap * pxPerM;
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
            // 窓が無ければ、インスペクタを範囲の左の端に置く (面の上の位置 a の板は、画面の x = 真ん中 + (a - 視点 / R) * R * pxPerM)
            inspectAngle = (AreaX + AreaW * AreaBoard * 0.5f - viewportW * 0.5f) / (Space.R * pxPerM) + viewX.Value / Space.R;
            return;
        }
        if (grab.Holding)
            return;
        // 窓の右の端を範囲の右の端に合わせる
        var right = WindowAngle(w, focus) * Space.R + focus.W * focus.Scale * 0.5f;
        viewTarget = right - (AreaX + AreaW - viewportW * 0.5f) / pxPerM;
    }

    // focus が移るか窓が動いたら、focus のある窓が見える所まで視点の行き先を動かす (列を動かした後は、次に動くまで追わない)
    static void FollowFocus(Workspace w, float halfViewport)
    {
        var window = w.FocusWindow;
        if (window == null || grab.Holding)
            return;
        var angle = WindowAngle(w, window);
        if (window == followed && angle == followedAngle && window.Scale == followedScale && BoardW == followedBoard && OldBoardW == followedOld)
            return;
        followed = window;
        followedAngle = angle;
        followedScale = window.Scale;
        followedBoard = BoardW;
        followedOld = OldBoardW;
        var half = window.W * window.Scale * 0.5f;
        // 窓の左の板も入れる (板は手前にあるので、面の上では大きく見える)
        var left = angle * Space.R - half - OldBoardW - (BoardW > 0 ? InspectGap + BoardW * Space.R / InspectR : 0);
        // 板と窓が両方は入らなければ、板の側を見せる (窓の右端は本文が少ない)
        var right = Math.Min(angle * Space.R + half, left + 2 * (halfViewport - FollowMargin));
        viewTarget = Space.FollowX(viewTarget, (left + right) * 0.5f, (right - left) * 0.5f + FollowMargin, halfViewport);
    }

    // 視点が追うときに窓の外に残す幅 (メートル)
    const float FollowMargin = 0.03f;

    // 控えている frame の帯 (足元)。
    // 止めていれば、止めた位置に印を出す
    // 窓が光を落とす床の高さと、円筒の軸からの距離 (原点は Recenter した両眼の中点。床の高さは仮)
    const float FloorY = -1.1f;
    const float FloorR = Space.R * 0.8f;

    // 足元の帯の幅と高さ
    const float TimelineWidth = 1.2f;
    const float TimelineY = -0.95f;
    const float TimelineH = 0.02f;
    // 止めた位置の印の幅と高さ
    const float TimelineMarkW = 0.012f;
    const float TimelineMarkH = 0.06f;
    // 範囲を決めているときに、帯と範囲の端の間に空ける px
    const float TimelineMarginPx = 12;
    // 帯を置く所: 中心の角度と高さ、半径、幅 (メートル)。範囲を決めていれば PlaceTimeline が決める
    static float timelineAngle;
    static float timelineY = TimelineY;
    static float timelineR = Space.R;
    static float timelineW = TimelineWidth;

    // 控えた frame の at 番目 (0 が直近) の、帯の上の位置 (帯の幅を 1 とし、真ん中が 0、右端が直近)
    static float TimelineX(int at, int count)
    {
        return 0.5f - (at + 0.5f) / count;
    }

    // 範囲を決めていれば、帯を範囲の下の端に範囲の幅で置く。窓とインスペクタは範囲の下の端まであるので、それより手前に置いて後から描く。
    // 範囲を決めていなければ足元に置く。
    // 画面の (sx, sy) に見える半径 r の点は、角度 (sx - 画面の真ん中) / (R pxPerM) + 視点 / R、高さ ((画面の真ん中 - sy) / (R pxPerM) - m12) r (m12 は縦のずらし)
    static void PlaceTimeline(float pxPerM, float m12)
    {
        if (AreaW <= 0)
        {
            timelineAngle = 0;
            timelineY = TimelineY;
            timelineR = Space.R;
            timelineW = TimelineWidth;
            return;
        }
        timelineR = InspectR - 0.01f;
        var perPx = timelineR / (Space.R * pxPerM);
        timelineAngle = (AreaX + AreaW * 0.5f - viewportW * 0.5f) / (Space.R * pxPerM) + viewX.Value / Space.R;
        timelineW = (AreaW - 2 * TimelineMarginPx) * perPx;
        var bottom = AreaY + AreaH - TimelineMarginPx - TimelineMarkH * 0.5f / Space.R / perPx * timelineR;
        timelineY = ((viewportH * 0.5f - bottom) / (Space.R * pxPerM) - m12) * timelineR;
    }

    static Spring Stiff()
    {
        var s = new Spring(0);
        s.Stiffness = 900;
        s.Damping = 60;
        return s;
    }

    static void DrawTimeline(Mat4 vp)
    {
        if (GameApp.Count == 0)
            return;
        // 手前に置いても足元と同じ大きさに見えるよう、半径に比例させる
        var k = timelineR / Space.R;
        Quad3d.Solid(vp * Space.OnCylinder(timelineAngle, timelineY, timelineR, timelineW, TimelineH * k), Color.Rgb(0.3f, 0.35f, 0.45f, 0.6f));
        if (!timelineShown)
            return;
        // 光の点は frame を送るたびに次の frame の所へ滑る
        Quad3d.Solid(vp * Space.OnCylinder(timelineAngle + timeline.Value * timelineW / timelineR, timelineY, timelineR, TimelineMarkW * k, TimelineMarkH * k),
            Color.Rgb(1.0f, 0.6f, 0.15f));
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

    static void Controllers(Workspace w, Ray aim, float dt)
    {
        var right = Xr.Input(1);
        var left = Xr.Input(0);
        pointer = Space.OnCylinderAt(aim, Space.R);
        // 押し離しは閾値を分けて、ぶれで繰り返さない
        var trigger = right != null && (triggerDown ? right.Trigger > 0.3f : right.Trigger > 0.7f);
        if (trigger && !triggerDown)
            Click(w, aim, true);
        else if (trigger)
            DragTo(w, aim);
        else if (triggerDown)
            ReleasePress(w);
        triggerDown = trigger;

        var rGrip = right != null && (rightGrip ? right.Grip > 0.3f : right.Grip > 0.7f);
        var lGrip = left != null && (leftGrip ? left.Grip > 0.3f : left.Grip > 0.7f);
        if (rGrip && !rightGrip && pointer != null)
        {
            var hit = WindowAt(w, aim);
            if (hit != null)
            {
                w.FocusWindow = hit.Window;
                grab.Start(hit.Window, w.CodeScreen.XAt(pointer.Angle), pointer.Y);
            }
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
            m.Step(WindowAngle(w, window), WindowY(w, window), window.Scale, grab.Window == window || (keyMoved == window && keyLift > 0),
                window == w.FocusWindow, dt);
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
            m.Step(m.Angle.Value, m.Y.Value, WindowMotion.SmallScale, false, false, dt);
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
        return m.Y.Value + (0.5f - v) * window.H * m.Scale.Value;
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
        Gfx.BeginPass(new PassOpts { Target = view.Target, Load = Gfx.LoadAction.Load });
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
        var side = Space.OffView(at.Angle, WindowAngle(w, window), window.W * window.Scale * 0.5f / Space.R, CueFov);
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
            var m = pair.Value;
            var a = m.Angle.Value;
            var at = Space.At(a, FloorY, FloorR);
            var size = pair.Key.W * m.Scale.Value * 1.4f;
            Quad3d.Soft(vp * Mat4.Translate(at) * Mat4.RotateY(Space.Facing(a)) * Mat4.RotateX(-(float)Math.PI / 2) * Mat4.Scale(new Vec3(size, size * 0.7f, 1)),
                Color.Rgb(0.35f, 0.75f, 1.0f, 0.06f + 0.06f * Math.Max(0, Math.Min(1, m.Glow.Value))));
        }
        foreach (var pair in motions)
        {
            var window = pair.Key;
            var m = pair.Value;
            var angle = m.Angle.Value;
            var y = m.Y.Value;
            var scale = m.Scale.Value;
            var panel = Panel(window);
            panel.Flat = lastEye == null;
            panel.FrameAngle = (particles.Next() - 0.5f) * 0.03f * m.Shake * m.Shake;
            panel.FrameY = (particles.Next() - 0.5f) * 0.05f * m.Shake * m.Shake;
            panel.Draw(window.Code.Doc, angle, y, scale, vp, m.Lift.Value, m.Glow.Value, m.Alarm);
            if (panel.OldShown)
                panel.DrawOld(vp, angle, y, scale, InspectR);
            foreach (var l in Landings)
                if (l.Window == window)
                    panel.DrawLanding(vp, window.Code.Doc, angle, y, scale, m.Lift.Value, l);
            if (m.CaretShown)
            {
                var id = window.Code.ViewId;
                var scroll = EditorHost.ScrollY(id);
                PanelView.CaretDrawn = false;
                panel.DrawCaret(vp, window.Code.Doc, angle, y, scale, m.Lift.Value, m.CaretX.Value, m.CaretY.Value - scroll);
                if (window == w.FocusWindow)
                    LogCaret(vp);
                // 2 つ目からの cursor (複数の選択) は滑らせない
                for (var i = 1; i < EditorHost.CaretCount(id); i++)
                    panel.DrawCaret(vp, window.Code.Doc, angle, y, scale, m.Lift.Value, EditorHost.CaretX(id, i), EditorHost.CaretY(id, i));
            }
            var r = Space.R - m.Lift.Value * PanelView.LiftDistance - PanelView.FrontGap - 0.005f;
            var width = window.W * scale * r / Space.R;
            // 保存の波: 明るい帯が上から下へ流れる
            if (m.Wave > 0)
                Quad3d.CurvedSolid(vp, Color.Rgb(0.6f, 0.95f, 1.0f, 0.45f * m.Wave), angle,
                    y + window.H * scale * (m.Wave - 0.5f), r, width, 0.025f * scale);
            // 沈み終えた行から上と下へ広がる波
            if (m.Ripple > 0)
            {
                var spread = (1 - m.Ripple) * window.PxH * 0.6f;
                var face = Space.R - m.Lift.Value * PanelView.LiftDistance - 0.003f;
                var band = 0.012f * scale;
                foreach (var py in new List<float> { m.RippleAt - spread, m.RippleAt + spread })
                    if (py >= 0 && py <= window.PxH)
                        Quad3d.CurvedSolid(vp, Color.Rgb(0.6f, 0.95f, 1.0f, 0.5f * m.Ripple), angle,
                            panel.YAt(y, scale, py) * face / Space.R, face, window.W * scale * face / Space.R, band);
            }
            // compile error の赤い光
            if (m.Alarm > 0)
                Quad3d.CurvedSolid(vp, Color.Rgb(1.0f, 0.2f, 0.15f, 0.25f * m.Alarm), angle, y, r, width,
                    window.H * scale);
            // 飛んだ先の行の印: 明るい帯を暗い線で挟む (背景によらず見える)
            if (m.Arrive > 0 && text != null && window == w.FocusWindow)
            {
                var cy = CursorY(window, m);
                var bandH = text.LineH / window.PxH * window.H * scale;
                Quad3d.CurvedSolid(vp, Color.Rgb(0.5f, 0.9f, 1.0f, 0.5f * m.Arrive), angle, cy, r, width, bandH);
                Quad3d.CurvedSolid(vp, Color.Rgb(0, 0, 0, 0.7f * m.Arrive), angle, cy + bandH * 0.55f, r, width, bandH * 0.1f);
                Quad3d.CurvedSolid(vp, Color.Rgb(0, 0, 0, 0.7f * m.Arrive), angle, cy - bandH * 0.55f, r, width, bandH * 0.1f);
            }
            // 打った行が一瞬光る
            if (m.Pulse > 0 && text != null && window == w.FocusWindow)
                Quad3d.CurvedSolid(vp, Color.Rgb(1.0f, 0.85f, 0.4f, 0.18f * m.Pulse), angle,
                    CursorY(window, m), r, width, text.LineH / window.PxH * window.H * scale);
        }
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
            Theme.Plate(vp, Theme.Surface, inspectAngle, InspectY, InspectR, InspectView.W * InspectScale, InspectView.H * InspectScale);
            Quad3d.Curved(vp, inspect.Texture, Color.Rgb(1, 1, 1, 1), inspectAngle, InspectY, InspectR, InspectView.W * InspectScale, InspectView.H * InspectScale, 0, 1);
        }
        DrawTimeline(vp);
        // コントローラーで指している円筒の上の点
        if (lastEye != null && pointer != null)
            Quad3d.Solid(vp * Space.OnCylinder(pointer.Angle, pointer.Y, Space.R - PanelView.FrontGap - 0.01f, 0.012f, 0.012f),
                Color.Rgb(0.4f, 0.9f, 1.0f, 0.9f));
        foreach (var ring in rings)
            DrawRing(vp, ring);
        DrawFlash(vp);
        DrawFocusCue(w, vp);
        DrawEdge(w, vp);
        hintCard.Draw(vp);
        AssetBoard.Draw(vp, searchAngle, Search.Shown ? searchTop - searchView.Height(SearchScale) : searchTop, SearchScale);
        if (Search.Shown)
            searchView.Draw(vp, searchAngle, searchTop, SearchScale);
        Quad3d.Opacity = 1;
    }
}
