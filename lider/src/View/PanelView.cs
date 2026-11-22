// One file zoomed in for reading and writing. For the body, the list of commands drawn by the editor view (EditorHost.Draw) is drawn with lider's MonoText.
// Each layer has its own texture (background, back, surface, front), redrawn only when the editor view's appearance or the Document changes.
// The surface has no background fill (the editor background is made transparent); the background is a board behind the back layer (filling the surface would hide the back layer).
// Changed lines are taken off the surface and lifted to the front layer until saved and a reload chunk is applied.
// The back layer shows with marks that there are lines differing from the version the game is running.
// Executed lines are shown under the surface as bands that fade with age and as marks beside the line numbers (redrawn every frame).
// Assigned values are shown to the right of the line's text as "target = value" at the front layer's height (redrawn every frame). They fade with age, flash when a value that had not changed for a while changes, and numbers show their recent values as a row of small dots.
// Field declaration lines show the current value in the same place, and class declaration lines show marks for the many instances.
// Load (LoadProfile) is shown at the same position on every line, separate from the background color (executed lines): normally the name of a resource in trouble at the right edge of the panel; while viewing the profile, the number for the selected resource in place of the line number.
// Line positions come from the editor's scroll and line height (EditorHost.LineY).
// The editor's CommandView (search etc.), NagView, and IME candidates are drawn on the front layer.
// A compile error of the saved version paints its line red on the back layer and shows the error message at the bottom of the front layer.
// When redrawing, the previously drawn commands are remembered per line (by position from the line's top edge), and if few lines changed, only those lines' bands are cleared and redrawn.
// When scrolled, the layer textures are shifted by the scroll amount (integer px), and only lines that came into view and lines that changed are redrawn
// (everything is redrawn when things not tied to lines, bands, or errors change, or when a character partially straddles a band).
// When a line moves between layers (lifted to the front by typing, sunk to the surface when a reload is applied), and for compile error lines, the line's band is cut out of the layer board and only the band is drawn separately and moved.
// Non-VR (Flat) has no binocular parallax, so layer depth is shown by shifting nearer layers toward the top left, casting the front layer's shadow on the surface, and fading the back layer.
// The scrollbar is not drawn into the texture (it could not be moved when the texture is shifted) but separately on the board. The cursor and typed-character motion are also drawn on the board (Lider slides them).
using System;
using System.Collections.Generic;
using Lub;
using Lubx;
using LiderEditor;

namespace LiderCore;

public class PanelView
{
    // Density at which text is about 15 px at 1.6 m (close to the Frame's pixel density and to a non-VR display with a 50° field of view).
    // If the texture is finer than the display, thin lines disappear when downscaled, since there are no mipmaps
    public const int BasePxW = 768;
    public const int BasePxH = 614;
    public const float BaseW = 1.1f;
    public const float BaseH = BaseW * BasePxH / BasePxW;
    // Current size of this panel (px). The text density does not change, so widening adds columns and lines
    public int PxW = BasePxW;
    public int PxH = BasePxH;
    public float W => BaseW * PxW / BasePxW;
    public float H => BaseW * PxH / BasePxW;
    // Title band attached above the panel (file name and close mark)
    public const int TitlePx = 28;
    public const float TitleH = BaseW * TitlePx / BasePxW;
    // Upper limit of layer texture density (texture width and height become this many times larger)
    const int MaxDensity = 3;

    // In non-XR, the number of pixels that 1 logical px of a panel with the default field of view and size covers on a display of height viewportH, rounded to an integer density.
    // With an integer, pixels stay aligned when shifted for scrolling. It is not raised if only slightly short (texture size grows with the square)
    public static int DensityFor(int viewportH)
    {
        var visible = 2 * Space.R * (float)Math.Tan(Space.FlatFov * 0.5f * Math.PI / 180);
        var d = viewportH * BaseH * Space.FlatScale / visible / BasePxH;
        return Math.Max(1, Math.Min(MaxDensity, (int)Math.Ceiling(d - 0.25f)));
    }

    // Layer spacing (m).
    // Gives about 0.1° of parallax at 1.6 m
    public const float FrontGap = 0.06f;
    public const float BackGap = 0.04f;
    public const float PlateGap = 0.08f;

    // Render target key per layer
    readonly string faceKey;
    readonly string frontKey;
    readonly string backKey;
    readonly string traceKey;
    readonly string valuesKey;
    readonly SpriteBatch batch;
    int sizedW = BasePxW;
    int sizedH = BasePxH;
    int drawnVersion = -1;
    int drawnRevision = -1;
    int drawnText = -1;
    int drawnDensity;
    // Layer texture density (matched to the text density in Declare)
    int density = 1;
    // Number of drawn commands (read with Cmd* after Draw)
    int count;
    // Font id → whether it is the icon font
    readonly Dictionary<int, bool> icon = new Dictionary<int, bool>();
    // Current clip
    float clipX;
    float clipY;
    float clipW;
    float clipH;

    // Editor view to draw
    public readonly int View;
    // Number of lines last redrawn (-1 if everything was redrawn)
    public int RedrawnLines = -1;
    // Only line bands are redrawn when the changed lines are at most this many
    const int MaxPartialLines = 6;
    // Previously drawn commands per line (non-text hash and text list), the list of things not tied to lines, and the scroll at that time
    float drawnScroll;
    // Amount to shift the texture first when redrawing bands (px, positive is up)
    int shiftBy;
    // Rects not tied to lines within this width of the right and bottom edges are treated as the scrollbar
    const float ScrollbarColumn = 16;
    // Commands not drawn (scrollbar), and the rect (x, y, width, height) and color of the scrollbar drawn on the board
    bool[] skip = new bool[0];
    readonly List<float> scrollbars = new List<float>();
    readonly List<int> scrollbarColors = new List<int>();
    Dictionary<int, int>? drawnHashes;
    Dictionary<int, List<string>>? drawnTexts;
    int drawnMiscHash;
    List<string> drawnMiscTexts = new List<string>();
    // Compile errors and reload notices for this file (Lider.InitNotices), and Workspace.ErrorsVersion (redraw when it changes)
    public List<BuildError> Errors = new List<BuildError>();
    public List<string> Notices = new List<string>();
    public int ErrorsVersion;
    int drawnErrors = -1;
    // Current values shown on field declaration lines (Lider decides them every frame)
    public List<FieldValue> Fields = new List<FieldValue>();
    // Line loads (passed by Lider; Enabled is false if not shown)
    public LoadProfile? Costs;

    // How to show the running version while the text differs from the version the game is running (switched with the command code:diff-side; true is side by side, false is inline)
    public static bool SideBySide = true;
    // Whether to show the running-version board (side-by-side form) (Lider decides before Prepare). While shown, executed-line glow and assigned values go on that board
    public bool OldShown;
    // Whether to insert unmatched lines of the running version into blank lines in the panel (inline form; Lider decides before Prepare, and EditorHost.SetGaps makes the blank lines)
    public bool Inline;
    bool inlineDrawn;
    int drawnGaps = -1;
    // Width of the running-version board (px). Line height and text density are the same as the panel
    public const int OldPxW = 520;
    public const float OldW = BaseW * OldPxW / BasePxW;
    // Gap between the running-version board and the panel (meters)
    public const float OldGap = 0.03f;
    // Width of the band at the board's right edge that connects corresponding lines (px)
    const int LinkPx = 28;
    readonly string oldKey;
    readonly SpriteBatch oldBatch;
    TextureRef? old;

    public PanelView(string key, int view)
    {
        View = view;
        faceKey = key + "_face";
        frontKey = key + "_front";
        backKey = key + "_back";
        traceKey = key + "_trace";
        valuesKey = key + "_values";
        batch = new SpriteBatch(key, PxW, PxH);
        oldKey = key + "_old";
        oldBatch = new SpriteBatch(oldKey, OldPxW, PxH);
    }

    // Converts a position in the board (0..1) to editor view px and left-clicks there (moves the cursor)
    public static void Click(Document doc, Vec2 uv)
    {
        EditorHost.Click(doc.Id, uv.X * BasePxW, uv.Y * BasePxH);
    }

    // Whether an editor command with this line number (EditorHost.CmdLine) is drawn on the front layer.
    // Bands and IME candidates (-1), and lines that differ from the version the game is running
    public static bool Front(Document doc, int line)
    {
        return line < 0 || (line > 0 && doc.LineChanged(line - 1));
    }

    readonly DeclareGap gap = new DeclareGap();

    // Height of the center of the title band
    public float TitleY(float y, float scale)
    {
        return y + (H + TitleH) * 0.5f * scale;
    }

    TextureRef? face;
    TextureRef? front;
    TextureRef? back;
    // Layer textures (after Prepare)
    public TextureRef? FaceTexture => face;
    public TextureRef? FrontTexture => front;
    public TextureRef? BackTexture => back;
    TextureRef? trace;
    bool traceDrawn;
    TextureRef? values;
    bool valuesDrawn;
    // Seconds taken to move between layers
    public const float MoveSeconds = 0.3f;
    // Lines moving between layers (line number → seconds since the move began), and whether the destination is the front
    readonly Dictionary<int, float> moving = new Dictionary<int, float>();
    readonly Dictionary<int, bool> movingToFront = new Dictionary<int, bool>();
    // Whether each visible line was in front when last drawn
    Dictionary<int, bool> drawnFront = new Dictionary<int, bool>();
    // Middle of the band of the line that last finished sinking into the surface (px in the view)
    public float SankAt;
    // Whether drawing for non-VR (Lider decides every frame)
    public bool Flat;
    // In non-VR, the angle and height by which the front layer (FrontGap from the surface) is shifted
    const float FlatShift = 0.0025f;
    // Amount by which only the frame and background shake (the body text does not shake; Lider decides before drawing)
    public float FrameAngle;
    public float FrameY;
    // Radius of the surface of the panel being drawn (decided inside Draw)
    float faceR = Space.R;

    // Angle and height offset for placing something at radius r in non-VR (the further it comes forward from the surface, the more toward the top left)
    float ShiftAngle(float r)
    {
        return Flat ? -FlatShift * (faceR - r) / FrontGap : 0;
    }

    float ShiftY(float r)
    {
        return Flat ? FlatShift * (faceR - r) / FrontGap : 0;
    }

    public bool Moving(int line)
    {
        return moving.ContainsKey(line);
    }

    public bool MovingToFront(int line)
    {
        return movingToFront.TryGetValue(line, out var f) && f;
    }

    // Advances the lines moving between layers by dt and returns the number of lines that finished sinking into the surface (the middle of that band is SankAt)
    public int Step(float dt)
    {
        var lines = new List<int>();
        foreach (var pair in moving)
            lines.Add(pair.Key);
        var sank = 0;
        var sum = 0.0f;
        var lh = EditorHost.LineHeight(View);
        foreach (var line in lines)
        {
            var age = moving[line] + dt;
            if (age < MoveSeconds)
            {
                moving[line] = age;
                continue;
            }
            if (!movingToFront[line])
            {
                sank++;
                sum += EditorHost.LineY(View, line) + lh * 0.5f;
            }
            moving.Remove(line);
            movingToFront.Remove(line);
        }
        if (sank > 0)
            SankAt = sum / sank;
        return sank;
    }

    // Declares the board textures, and the shader and glyph atlas used for redrawing.
    // lub discards resources whose declaration lapsed, so call this in every frame that draws (even when only drawing without redrawing, as for a panel in its closing animation).
    // The shader and atlas too: if nothing is typed for a while, they would be compiled or uploaded again on the next keystroke
    public void Declare(MonoText text)
    {
        if (gap.Lapsed())
        {
            // Redraw everything in the next Prepare
            drawnVersion = -1;
            drawnText = -1;
        }
        if (sizedW != PxW || sizedH != PxH)
        {
            sizedW = PxW;
            sizedH = PxH;
            drawnVersion = -1;
            drawnText = -1;
            batch.LogicalW = PxW;
            batch.LogicalH = PxH;
            oldBatch.LogicalH = PxH;
        }
        density = text.Density;
        var pw = PxW * density;
        var ph = PxH * density;
        face = Assets.RenderTarget(faceKey, pw, ph, Gfx.PixelFormat.Rgba8);
        front = Assets.RenderTarget(frontKey, pw, ph, Gfx.PixelFormat.Rgba8);
        back = Assets.RenderTarget(backKey, pw, ph, Gfx.PixelFormat.Rgba8);
        trace = Assets.RenderTarget(traceKey, pw, ph, Gfx.PixelFormat.Rgba8);
        values = Assets.RenderTarget(valuesKey, pw, ph, Gfx.PixelFormat.Rgba8);
        old = OldShown ? Assets.RenderTarget(oldKey, OldPxW * density, ph, Gfx.PixelFormat.Rgba8) : null;
        batch.Ensure();
        oldBatch.Ensure();
        text.Glyphs.Ensure();
        DeclareShaders();
        scratch = Assets.RenderTarget("lider_panel_scratch_" + pw + "x" + ph, pw, ph, Gfx.PixelFormat.Rgba8);
    }

    // Declares the texture and redraws if the content changed (call outside the pass to the display)
    public bool Prepare(Document doc, MonoText text, TraceLog log)
    {
        Declare(text);
        if (face == null || front == null || back == null)
            return false;
        var version = EditorHost.Version(View);
        var gapsVersion = EditorHost.GapsVersion(View);
        if (version != drawnVersion || doc.Revision != drawnRevision || text.Version != drawnText
            || ErrorsVersion != drawnErrors || density != drawnDensity || gapsVersion != drawnGaps)
        {
            // When the blank lines change, line heights shift, so bands alone cannot be redrawn
            var partial = text.Version == drawnText && ErrorsVersion == drawnErrors && Errors.Count == 0 && Notices.Count == 0 && density == drawnDensity
                && gapsVersion == drawnGaps;
            Render(doc, text, face, front, back, partial);
            drawnVersion = version;
            drawnRevision = doc.Revision;
            drawnText = text.Version;
            drawnErrors = ErrorsVersion;
            drawnDensity = density;
            drawnGaps = gapsVersion;
        }
        var active = log.Active(doc.Path);
        if (trace != null && (active || traceDrawn || Inline || inlineDrawn))
        {
            RenderTrace(doc, text, log, trace);
            traceDrawn = active;
            inlineDrawn = Inline;
        }
        if (values != null && (active || valuesDrawn || Fields.Count > 0))
        {
            RenderValues(doc, text, log, values);
            valuesDrawn = active || Fields.Count > 0;
        }
        if (old != null)
            RenderOld(doc, text, log, old);
        return true;
    }

    // Draws into the pass to the display, within the frame that was Prepared.
    // scale is the panel size factor, z is depth (meters, negative is nearer), glow is the edge glow (0 to 1), shake is the error-line shake (0 to 1)
    public void Draw(Document doc, float angle, float y, float scale, Mat4 vp, float z, float glow, float shake)
    {
        if (face == null || front == null || back == null)
            return;
        var white = Color.Rgb(1, 1, 1);
        var r = Space.R + z;
        // Lift of the grabbed panel (0 to 1)
        var lift = -z / Grab.LiftDistance;
        faceR = r;
        var plate = r + PlateGap;
        var k = plate / Space.R;
        // Put the title and body on a single background.
        // The focus edge is a thin line plus a glow fading outward; while lifted, the background is more opaque
        var plateY = (y + FrameY + TitleH * 0.5f * scale) * k;
        var plateW = W * scale * k;
        var plateH = (H + TitleH) * scale * k;
        var clamped = Math.Max(0, Math.Min(1, glow));
        if (glow > 0.01f)
        {
            Quad3d.Round(vp, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.14f * clamped), angle + FrameAngle, plateY, plate + 0.006f, plateW + 0.05f, plateH + 0.05f, Theme.Corner + 0.025f, 0.025f);
            Quad3d.Round(vp, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.75f * clamped), angle + FrameAngle, plateY, plate + 0.002f, plateW + 0.004f, plateH + 0.004f, Theme.Corner + 0.002f, 0);
        }
        Theme.Plate(vp, Color.Rgb(Theme.Surface.R, Theme.Surface.G, Theme.Surface.B, Math.Min(0.97f, 0.72f + 0.18f * clamped + 0.1f * lift)), angle + FrameAngle, plateY, plate, plateW, plateH);
        var titleY = TitleY(y, scale);
        Quad3d.CurvedSolid(vp, Theme.Hairline, angle + FrameAngle, (titleY + FrameY - TitleH * 0.5f * scale) * k, plate, plateW, 0.0015f);
        Layer(vp, back, Flat ? Color.Rgb(1, 1, 1, 0.5f) : white, angle, y, scale, r + BackGap, null);
        if (trace != null)
            Layer(vp, trace, white, angle, y, scale, r, null);
        // Bands of lines moving between layers and of error lines (line number, top and bottom v in the texture, whether in the front layer)
        var strips = new List<int>();
        var errorLines = new Dictionary<int, bool>();
        foreach (var e in Errors)
            errorLines[e.Line + 1] = true;
        var lh = EditorHost.LineHeight(View);
        var faceHoles = new List<float>();
        var frontHoles = new List<float>();
        for (var line = EditorHost.FirstLine(View); line <= EditorHost.LastLine(View); line++)
        {
            var moves = moving.ContainsKey(line);
            if (!moves && !errorLines.ContainsKey(line))
                continue;
            var top = Math.Max(0, EditorHost.LineY(View, line));
            var bottom = Math.Min(PxH, EditorHost.LineY(View, line) + lh);
            if (bottom <= top)
                continue;
            var up = moves ? movingToFront[line] : Front(doc, line);
            var holes = up ? frontHoles : faceHoles;
            holes.Add(top / PxH);
            holes.Add(bottom / PxH);
            strips.Add(line);
        }
        Layer(vp, face, white, angle, y, scale, r, faceHoles);
        Scrollbars(vp, angle, y, scale, r);
        // Lines moving between layers move from the original layer to the destination layer. Error lines are red and sway sideways while shaking
        foreach (var line in strips)
        {
            var top = Math.Max(0, EditorHost.LineY(View, line)) / PxH;
            var bottom = Math.Min(PxH, EditorHost.LineY(View, line) + lh) / PxH;
            if (moving.TryGetValue(line, out var age))
            {
                var up = movingToFront[line];
                var t = Math.Min(1, age / MoveSeconds);
                var e = 1 - (1 - t) * (1 - t) * (1 - t);
                var from = up ? r : r - FrontGap;
                var to = up ? r - FrontGap : r;
                Piece(vp, up ? front : face, errorLines.ContainsKey(line) ? ErrorTint : white, angle, y, scale, from + (to - from) * e, top, bottom);
            }
            else
            {
                var a = angle + 0.004f * shake * (float)Math.Sin(Sys.Seconds() * 70);
                Piece(vp, Front(doc, line) ? front : face, ErrorTint, a, y, scale, Front(doc, line) ? r - FrontGap : r, top, bottom);
            }
        }
        // In non-VR, the front layer's shadow is cast toward the bottom right on the surface
        if (Flat)
            Layer(vp, front, Color.Rgb(0, 0, 0, 0.45f), angle + FlatShift, y - FlatShift, scale, r, frontHoles);
        Layer(vp, front, white, angle, y, scale, r - FrontGap, frontHoles);
        if (values != null && valuesDrawn)
            Layer(vp, values, white, angle, y, scale, r - FrontGap, null);
    }

    // Text color of compile error lines
    static readonly Color ErrorTint = Color.Rgb(1.0f, 0.45f, 0.4f);

    // Cylinder angle for x px in the view
    public float AngleAt(float angle, float scale, float x)
    {
        return angle + (x / PxW - 0.5f) * W * scale / Space.R;
    }

    // Height on the surface (radius Space.R) for py px in the view
    public float YAt(float y, float scale, float py)
    {
        return y + (0.5f - py / PxH) * H * scale;
    }

    // Top and bottom ends of the cursor last drawn by DrawCaret (positions in space). CaretDrawn is false if not drawn
    public static Vec3 CaretTop;
    public static Vec3 CaretBottom;
    public static bool CaretDrawn;

    // Cursor drawn outside the panel texture (x and top are px in the view).
    // Placed on the same layer as the line beneath it (the front if the line is lifted to the front)
    public void DrawCaret(Mat4 vp, Document doc, float angle, float y, float scale, float z, float x, float top)
    {
        var lh = EditorHost.LineHeight(View);
        if (lh <= 0 || top < 0 || top + lh > PxH || x < 0 || x > PxW)
            return;
        // Top and bottom ends of the cursor (what the off-screen recording tool looks at; LIDER_CARET_LOG)
        CaretTop = Space.At(AngleAt(angle, scale, x), YAt(y, scale, top), Space.R);
        CaretBottom = Space.At(AngleAt(angle, scale, x), YAt(y, scale, top + lh), Space.R);
        CaretDrawn = true;
        // The last line whose top edge is above the middle of the cursor (with blank lines, it cannot be found by dividing by the line height)
        var line = EditorHost.FirstLine(View);
        for (var l = line + 1; l <= EditorHost.LastLine(View) && EditorHost.LineY(View, l) <= top + lh * 0.5f; l++)
            line = l;
        var r = Space.R + z - (Front(doc, line) ? FrontGap : 0);
        var k = r / Space.R;
        var w = EditorHost.CaretWidth();
        Quad3d.CurvedSolid(vp, Unpack(EditorHost.CaretColor()), AngleAt(angle, scale, x + w * 0.5f) + ShiftAngle(r), (YAt(y, scale, top + lh * 0.5f) + ShiftY(r)) * k, r,
            w / PxW * W * scale * k, lh / PxH * H * scale * k);
    }

    // Typed characters: draw the same part of that line's layer texture lifted in front and larger, settle it into the layer over Landing.Seconds, then remove it
    public void DrawLanding(Mat4 vp, Document doc, float angle, float y, float scale, float z, Landing l)
    {
        var front = Front(doc, l.Line);
        var tex = front ? this.front : face;
        var lh = EditorHost.LineHeight(View);
        var top = EditorHost.LineY(View, l.Line);
        if (tex == null || top < 0 || top + lh > PxH)
            return;
        var t = Math.Min(1, l.Age / Landing.Seconds);
        var e = 1 - (1 - t) * (1 - t) * (1 - t);
        var r = Space.R + z - (front ? FrontGap : 0) - 0.03f * (1 - e);
        var k = r / Space.R;
        var grow = 1 + 0.35f * (1 - e);
        var uv = new List<float> { l.X0 / PxW, top / PxH, l.X1 / PxW, (top + lh) / PxH };
        Quad3d.Part(vp * Space.OnCylinder(AngleAt(angle, scale, (l.X0 + l.X1) * 0.5f) + ShiftAngle(r), (YAt(y, scale, top + lh * 0.5f) + ShiftY(r)) * k, r,
            (l.X1 - l.X0) / PxW * W * scale * k * grow, lh / PxH * H * scale * k * grow), tex, Color.Rgb(1, 1, 1, 1 - e), uv);
    }

    // Places the editor's scrollbar rect curved, at the same place as the surface
    void Scrollbars(Mat4 vp, float angle, float y, float scale, float r)
    {
        var k = r / Space.R;
        var span = W * scale / Space.R;
        for (var i = 0; i < scrollbarColors.Count; i++)
        {
            var x0 = scrollbars[i * 4];
            var y0 = scrollbars[i * 4 + 1];
            var w = scrollbars[i * 4 + 2];
            var h = scrollbars[i * 4 + 3];
            var a = angle + ((x0 + w * 0.5f) / PxW - 0.5f) * span;
            var cy = y * k + (0.5f - (y0 + h * 0.5f) / PxH) * H * scale * k;
            Quad3d.CurvedSolid(vp, Unpack(scrollbarColors[i]), a, cy, r, w / PxW * span * r, h / PxH * H * scale * k);
        }
    }

    // Places a layer of radius r curved along the cylinder, at a size that spans the same angle from the viewer (origin) as the surface.
    // The layer difference becomes only binocular parallax, and with one eye the text overlaps the surface
    // holes are the parts to cut out (pairs of top and bottom v from the texture top, listed from the top)
    void Layer(Mat4 vp, TextureRef tex, Color tint, float angle, float y, float scale, float r, List<float>? holes)
    {
        var at = 0.0f;
        if (holes != null)
        {
            for (var i = 0; i < holes.Count; i += 2)
            {
                Piece(vp, tex, tint, angle, y, scale, r, at, holes[i]);
                at = holes[i + 1];
            }
        }
        Piece(vp, tex, tint, angle, y, scale, r, at, 1);
    }

    // Places the vertical band v0 to v1 of the layer texture at the same place on the board
    void Piece(Mat4 vp, TextureRef tex, Color tint, float angle, float y, float scale, float r, float v0, float v1)
    {
        if (v1 <= v0)
            return;
        var k = r / Space.R;
        Quad3d.Curved(vp, tex, tint, angle + ShiftAngle(r), (YAt(y, scale, (v0 + v1) * 0.5f * PxH) + ShiftY(r)) * k, r, W * scale * k,
            (v1 - v0) * H * scale * k, v0, v1);
    }

    void Begin(TextureRef target)
    {
        Gfx.BeginPass(new PassOpts { Target = target, ClearColor = new float[] { 0, 0, 0, 0 } });
        batch.Begin();
    }

    void End()
    {
        batch.Flush();
        Gfx.EndPass();
    }

    void RenderTrace(Document doc, MonoText text, TraceLog log, TextureRef target)
    {
        Begin(target);
        // SpriteBatch draws in the order atlases are first used, so fill (transparent) before the text
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var lh = EditorHost.LineHeight(View);
        var x0 = EditorHost.TextX(View);
        var last = EditorHost.LastLine(View);
        for (var line = EditorHost.FirstLine(View); line <= last; line++)
            Glow(OldShown ? 0 : TraceLog.Glow(AgeAt(doc, log, line)), EditorHost.LineY(View, line), x0, lh);
        if (Inline)
            RenderInline(doc, text, log, x0, lh);
        End();
    }

    // Executed-line glow (marks beside the line numbers and faint bands under the text)
    void Glow(float glow, float y, float x0, float lh)
    {
        if (glow <= 0)
            return;
        batch.Rect(2, y + 2, 6, lh - 4, Color.Rgb(1.0f, 0.6f, 0.15f, 0.25f + 0.75f * glow));
        batch.Rect(x0 - 6, y, PxW - x0 + 6, lh, Color.Rgb(1.0f, 0.6f, 0.15f, 0.14f * glow));
    }

    // Inline form: draws the unmatched lines of the running version in the blank lines, with a red background, glow, and assigned values
    void RenderInline(Document doc, MonoText text, TraceLog log, float x0, float lh)
    {
        var olds = doc.InlineOld();
        for (var i = 0; i < olds.Count; i++)
        {
            var y = InlineY(doc, i);
            if (y + lh < 0 || y > PxH)
                continue;
            var body = doc.LiveLines[olds[i]];
            batch.Rect(x0 - 6, y, PxW - x0 + 6, lh, ChangedTint);
            Glow(TraceLog.Glow(log.Age(doc.Path, olds[i] + 1)), y, x0, lh);
            text.Draw(batch, body, x0, y, InlineInk, 0, PxW);
            DrawValues(batch, text, log, log.ValuesAt(doc.Path, olds[i] + 1), x0 + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * text.CellW, y, lh, PxW);
        }
    }

    // Text color of inserted running-version lines (fainter than the body, reddish)
    static readonly Color InlineInk = Color.Rgb(0.85f, 0.55f, 0.55f);

    // Top edge y (px) of the i-th running-version line inserted in the inline form (in Document.InlineOld order)
    public float InlineY(Document doc, int i)
    {
        var at = doc.InlineAt();
        var k = 0;
        for (var j = i - 1; j >= 0 && at[j] == at[i]; j--)
            k++;
        return EditorHost.LineY(View, at[i]) - (doc.InlineGaps()[at[i]] - k) * EditorHost.LineHeight(View);
    }

    // How many frames ago the version the game is running executed line line (from 1) of the panel (-1 for changed lines, added lines, and lines not executed).
    // Executed lines and values arrive with running-version line numbers, so the text line is mapped to the running-version line for lookup
    public static int AgeAt(Document doc, TraceLog log, int line)
    {
        var live = doc.LiveLine(line - 1);
        return live < 0 ? -1 : log.Age(doc.Path, live + 1);
    }

    // Values assigned by the version the game is running on line line (from 1) of the panel (none for changed and added lines)
    public static List<TracedValue> ValuesAt(Document doc, TraceLog log, int line)
    {
        var live = doc.LiveLine(line - 1);
        return live < 0 ? new List<TracedValue>() : log.ValuesAt(doc.Path, live + 1);
    }

    // Number of frames a value glows after it changes.
    // So that values changing every frame do not glow continuously, it glows only when the previous value lasted this long
    const int ChangeGlowFrames = 20;
    // Width of the dot row for numeric values (characters)
    const int SparkCells = 6;

    void RenderValues(Document doc, MonoText text, TraceLog log, TextureRef target)
    {
        Begin(target);
        // SpriteBatch draws in the order atlases are first used, so fill (transparent) before the text
        batch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var lh = EditorHost.LineHeight(View);
        var x0 = EditorHost.TextX(View);
        var last = EditorHost.LastLine(View);
        tagLines.Clear();
        tagLeft.Clear();
        tagRight.Clear();
        foreach (var f in Fields)
            RenderField(doc, text, f, x0, lh);
        for (var line = EditorHost.FirstLine(View); !OldShown && line <= last; line++)
        {
            var live = doc.LiveLine(line - 1);
            var cost = live < 0 || Costs == null || !Costs.Enabled ? null : Costs.At(doc.Path, live + 1);
            var y = EditorHost.LineY(View, line);
            if (cost != null && Costs!.Viewing)
                DrawColumn(text, cost, line, x0, y, lh);
            else if (cost != null)
                DrawTags(text, cost, line, y, lh);
            var shown = ValuesAt(doc, log, line);
            if (shown.Count == 0)
                continue;
            var body = doc.Text(line - 1);
            DrawValues(batch, text, log, shown, x0 + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * text.CellW, y, lh, PxW);
        }
        End();
    }

    // Number of characters of the number shown in place of the line number while viewing the profile ("12.3")
    public const int NumberCells = 4;

    // Width of one normal tag (characters)
    const int TagCells = 3;

    // Lines of the drawn load column or tags, and their left and right edges (px)
    readonly List<int> tagLines = new List<int>();
    readonly List<float> tagLeft = new List<float>();
    readonly List<float> tagRight = new List<float>();

    // If a position in the panel (px) is on the load column or a tag, that line (from 1; -1 if none)
    public int CostLineAt(float x, float y)
    {
        var lh = EditorHost.LineHeight(View);
        for (var i = 0; i < tagLines.Count; i++)
        {
            var ly = EditorHost.LineY(View, tagLines[i]);
            if (y >= ly && y < ly + lh && x >= tagLeft[i] && x < tagRight[i])
                return tagLines[i];
        }
        return -1;
    }

    void Hit(int line, float left, float right)
    {
        tagLines.Add(line);
        tagLeft.Add(left);
        tagRight.Add(right);
    }

    // While viewing the profile: in place of the line number (the editor does not draw line numbers), shows the number for the selected resource aligned to the same right edge as line numbers, with a right-aligned bar in the background
    void DrawColumn(MonoText text, LineCost c, int line, float x0, float y, float lh)
    {
        var p = Costs!;
        var m = p.Metric;
        if (!LoadProfile.Shows(c, m))
            return;
        var right = x0 - Style.PaddingX;
        var w = x0 - 2 * Style.PaddingX;
        var level = LoadProfile.Level(c, m);
        var value = LoadProfile.Value(c, m);
        LoadBar.Fill(batch, level, value, p.Max(m), right - w, y, w, lh, 0.3f);
        LoadBar.Right(batch, text, LoadProfile.Number(value, m), right, y, level == LoadProfile.Calm ? Theme.Muted : Theme.Load(level));
        Hit(line, right - w, right);
    }

    // Normally: lists the names of resources in trouble in the order CPU, MEM, GPU, packed and aligned to the right edge of the panel
    void DrawTags(MonoText text, LineCost c, int line, float y, float lh)
    {
        var cw = text.CellW;
        var right = PxW - ScrollbarColumn - 6;
        var names = new List<string>();
        for (var m = 0; m < 3; m++)
            if (LoadProfile.Level(c, m) == LoadProfile.Hot)
                names.Add(LoadProfile.MetricNames[m]);
        if (names.Count == 0)
            return;
        var x = right - (names.Count * (TagCells + 1) - 1) * cw;
        var left = x;
        foreach (var name in names)
        {
            batch.Rect(x - 2, y + 2, TagCells * cw + 4, lh - 4, Color.Rgb(0.3f, 0.07f, 0.16f, 0.95f));
            text.Draw(batch, name, x, y, Theme.Load(LoadProfile.Hot), 0, PxW);
            x += (TagCells + 1) * cw;
        }
        Hit(line, left - 2, right + 2);
    }

    // Lays out "target = value" to the right (x) of the line text (nothing is drawn beyond maxX). Returns the right x after laying out
    float DrawValues(SpriteBatch b, MonoText text, TraceLog log, List<TracedValue> shown, float x, float y, float lh, float maxX)
    {
        foreach (var v in shown)
        {
            if (x >= maxX)
                break;
            var s = v.Label + " = " + v.Text;
            var w = Utf8.Cells(s, Utf8.Bytes(s)) * text.CellW;
            var glow = TraceLog.Glow(log.Frame - v.At);
            var changed = v.ChangedAt < 0 || v.Steady < ChangeGlowFrames ? 0 : Math.Max(0, 1 - (float)(log.Frame - v.ChangedAt) / ChangeGlowFrames);
            b.Rect(x - 4, y + 1, Math.Min(w + 8, maxX - x + 4), lh - 2, Color.Rgb(0.08f + 0.3f * changed, 0.1f + 0.25f * changed, 0.14f, (0.55f + 0.35f * changed) * glow));
            text.Draw(b, s, x, y, Color.Rgb(Theme.Value.R, Theme.Value.G, Theme.Value.B, 0.35f + 0.65f * glow), 0, maxX);
            x += w + text.CellW;
            if (v.Count >= 2 && x + SparkCells * text.CellW <= maxX)
            {
                Spark(b, v, log.Frame, x, y + 3, SparkCells * text.CellW, lh - 6, glow);
                x += (SparkCells + 1) * text.CellW;
            }
            x += text.CellW;
        }
        return x;
    }

    // Panel height (px) at which line 0 (from 0) of the running version is placed.
    // Running-version lines are packed together, and the cursor line or a corresponding line near it is aligned to the same height as in the panel
    public float OldTop(Document doc)
    {
        var lh = EditorHost.LineHeight(View);
        var map = doc.LiveMap();
        var c = doc.Line;
        for (var k = 0; k < map.Length; k++)
        {
            if (c - k >= 0 && c - k < map.Length && map[c - k] >= 0)
                return EditorHost.LineY(View, c - k + 1) - map[c - k] * lh;
            if (c + k < map.Length && map[c + k] >= 0)
                return EditorHost.LineY(View, c + k + 1) - map[c + k] * lh;
        }
        return EditorHost.LineY(View, 1);
    }

    // Running-version board: unmatched lines (changed and deleted lines) are red, executed lines glow, assigned values are shown,
    // and the band at the right edge connects, for each change, the running-version line range with the panel line range
    void RenderOld(Document doc, MonoText text, TraceLog log, TextureRef target)
    {
        Gfx.BeginPass(new PassOpts { Target = target, ClearColor = new float[] { 0, 0, 0, 0 } });
        oldBatch.Begin();
        // SpriteBatch draws in the order atlases are first used, so fill (transparent) before the text
        oldBatch.Rect(0, 0, 1, 1, Color.Rgb(0, 0, 0, 0));
        var lh = EditorHost.LineHeight(View);
        var top = OldTop(doc);
        var map = doc.LiveMap();
        var lines = doc.LiveLines;
        var kept = new bool[lines.Count];
        foreach (var l in map)
            if (l >= 0)
                kept[l] = true;
        var maxX = OldPxW - LinkPx;
        var x0 = text.CellW;
        var first = Math.Max(0, (int)Math.Floor(-top / lh));
        var last = Math.Min(lines.Count - 1, (int)Math.Ceiling((PxH - top) / lh));
        for (var i = first; i <= last; i++)
        {
            var y = top + i * lh;
            if (!kept[i])
                oldBatch.Rect(0, y, maxX, lh, ChangedTint);
            var glow = TraceLog.Glow(log.Age(doc.Path, i + 1));
            if (glow > 0)
            {
                oldBatch.Rect(0, y + 2, 4, lh - 4, Color.Rgb(1.0f, 0.6f, 0.15f, 0.25f + 0.75f * glow));
                oldBatch.Rect(0, y, maxX, lh, Color.Rgb(1.0f, 0.6f, 0.15f, 0.14f * glow));
            }
            var body = lines[i];
            text.Draw(oldBatch, body, x0, y, kept[i] ? Theme.Muted : ErrorTint, 0, maxX);
            DrawValues(oldBatch, text, log, log.ValuesAt(doc.Path, i + 1), x0 + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * text.CellW, y, lh, maxX);
        }
        // Height of the top edge of a panel line (from 0) (extended at the same spacing beyond the text lines)
        var newTop = EditorHost.LineY(View, 1);
        var prevNew = -1;
        var prevOld = -1;
        for (var line = 0; line <= map.Length; line++)
        {
            var o = line < map.Length ? map[line] : lines.Count;
            if (o < 0)
                continue;
            if (line - prevNew > 1 || o - prevOld > 1)
                Link(top + (prevOld + 1) * lh, top + o * lh, newTop + (prevNew + 1) * lh, newTop + line * lh, maxX);
            prevNew = line;
            prevOld = o;
        }
        oldBatch.Flush();
        Gfx.EndPass();
    }

    // Background of changed lines
    static readonly Color ChangedTint = Color.Rgb(1.0f, 0.45f, 0.45f, 0.16f);

    // Connects heights old0..old1 at the band's left edge with new0..new1 at its right edge (a missing side becomes a thin line)
    void Link(float old0, float old1, float new0, float new1, float x0)
    {
        if (Math.Max(old1, new1) < 0 || Math.Min(old0, new0) > PxH)
            return;
        var span = OldPxW - x0;
        for (var x = 0; x < span; x += 2)
        {
            var t = (x + 1.0f) / span;
            var y0 = old0 + (new0 - old0) * t;
            var y1 = old1 + (new1 - old1) * t;
            oldBatch.Rect(x0 + x, y0, 2, Math.Max(2, y1 - y0), Color.Rgb(1.0f, 0.45f, 0.45f, 0.45f));
        }
    }

    // Draws the running-version board at radius r, with center angle angle and height y, width w and height h (meters at radius Space.R)
    public void DrawOld(Mat4 vp, float angle, float y, float w, float h, float r)
    {
        if (old == null)
            return;
        var k = r / Space.R;
        Theme.Plate(vp, Theme.Surface, angle, y * k, r, w * k, h * k);
        Quad3d.Curved(vp, old, Color.Rgb(1, 1, 1, 1), angle, y * k, r, w * k, h * k, 0, 1);
    }

    // To the right of declaration line text, shows "= value" for a field or instance marks for a class, and remembers where the marks are (to select by pressing)
    // Width of one character of the text used to draw values (ValueAt uses it to find where it drew)
    float valueCellW = 1;

    void RenderField(Document doc, MonoText text, FieldValue f, float x0, float lh)
    {
        valueCellW = text.CellW;
        var body = doc.Text(f.Line - 1);
        var x = x0 + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * text.CellW;
        var y = EditorHost.LineY(View, f.Line);
        if (f.Text != "")
        {
            var s = "= " + f.Text;
            var w = Math.Min(PxW - x, Utf8.Cells(s, Utf8.Bytes(s)) * text.CellW);
            // A changed value gets a strongly glowing background and its text bounces a little
            batch.Rect(x - 4, y + 1, w + 8, lh - 2, Color.Rgb(0.08f + 0.8f * f.Glow, 0.1f + 0.5f * f.Glow, 0.14f, 0.55f + 0.45f * f.Glow));
            text.Draw(batch, s, x, y - 4 * f.Glow, f.Glow > 0.5f ? Color.Rgb(0.1f, 0.06f, 0.02f) : Theme.Value, 0, PxW);
            if (f.History != null)
                Spark(f.History, x + w + text.CellW, y + 3, SparkCells * text.CellW, lh - 6);
        }
        foreach (var m in f.Marks)
        {
            m.X0 = x;
            m.X1 = x + Utf8.Cells(m.Text, Utf8.Bytes(m.Text)) * text.CellW;
            if (m.Selected)
                batch.Rect(m.X0 - 3, y + 1, m.X1 - m.X0 + 6, lh - 2, Theme.Selected);
            text.Draw(batch, m.Text, x, y, Theme.Accent, 0, PxW);
            x = m.X1 + text.CellW;
        }
    }

    // Field value at (x, y) px in the panel (the "= value" after the line; null if none)
    public FieldValue? ValueAt(Document doc, float x, float y)
    {
        var lh = EditorHost.LineHeight(View);
        foreach (var f in Fields)
        {
            var top = EditorHost.LineY(View, f.Line);
            if (f.Text == "" || f.Path == "" || y < top || y >= top + lh)
                continue;
            var body = doc.Text(f.Line - 1);
            var left = EditorHost.TextX(View) + (Utf8.Cells(body, Utf8.Bytes(body)) + 2) * valueCellW;
            var s = "= " + f.Text;
            if (x >= left - 4 && x <= left + Utf8.Cells(s, Utf8.Bytes(s)) * valueCellW + 4)
                return f;
        }
        return null;
    }

    // Class line mark at (x, y) px in the panel
    public FieldMark? MarkAt(float x, float y)
    {
        var lh = EditorHost.LineHeight(View);
        foreach (var f in Fields)
        {
            var top = EditorHost.LineY(View, f.Line);
            if (y < top || y >= top + lh)
                continue;
            foreach (var m in f.Marks)
                if (x >= m.X0 && x <= m.X1)
                    return m;
        }
        return null;
    }

    // Draws the last Fade frames of a numeric value as dots whose heights follow the order of the values, oldest on the left (Fade frames ago), this frame on the right
    void Spark(SpriteBatch b, TracedValue v, int frame, float x, float y, float w, float h, float glow)
    {
        var lo = float.MaxValue;
        var hi = float.MinValue;
        for (var i = 0; i < v.Count; i++)
        {
            lo = Math.Min(lo, v.Samples[i]);
            hi = Math.Max(hi, v.Samples[i]);
        }
        if (hi <= lo)
            return;
        for (var i = 0; i < v.Count; i++)
        {
            var age = frame - v.SampleAt[i];
            if (age >= TraceLog.Fade)
                continue;
            var px = x + w * (1 - (age + 1.0f) / TraceLog.Fade);
            var py = y + h * (1 - (v.Samples[i] - lo) / (hi - lo));
            b.Rect(px, py - 1, 2, 2, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.8f * glow));
        }
    }

    // Draws a field's recent values as the same dot row as assigned values
    void Spark(List<float> history, float x, float y, float w, float h)
    {
        var lo = float.MaxValue;
        var hi = float.MinValue;
        foreach (var v in history)
            if (v == v)
            {
                lo = Math.Min(lo, v);
                hi = Math.Max(hi, v);
            }
        if (hi <= lo)
            return;
        for (var i = 0; i < history.Count; i++)
        {
            var v = history[i];
            if (v != v)
                continue;
            var age = history.Count - 1 - i;
            var px = x + w * (1 - (age + 1.0f) / TraceLog.Fade);
            var py = y + h * (1 - (v - lo) / (hi - lo));
            batch.Rect(px, py - 1, 2, 2, Color.Rgb(Theme.Accent.R, Theme.Accent.G, Theme.Accent.B, 0.8f));
        }
    }

    void Render(Document doc, MonoText text, TextureRef face, TextureRef front, TextureRef back, bool partial)
    {
        count = EditorHost.Draw(View, PxW, PxH);
        var lh = EditorHost.LineHeight(View);
        var x0 = EditorHost.TextX(View);
        var first = EditorHost.FirstLine(View);
        var last = EditorHost.LastLine(View);
        // Lines whose layer changed since the last drawing start moving between layers
        var nowFront = new Dictionary<int, bool>();
        for (var line = first; line <= last; line++)
        {
            var up = Front(doc, line);
            nowFront[line] = up;
            if (drawnFront.TryGetValue(line, out var was) && was != up)
            {
                moving[line] = 0;
                movingToFront[line] = up;
            }
        }
        drawnFront = nowFront;
        // Line number → whether it goes in front
        var lifted = new Dictionary<int, bool>();
        var layer = new bool[count];
        var barX0 = 1e9f;
        var barY0 = 1e9f;
        var barX1 = -1e9f;
        var barY1 = -1e9f;
        skip = new bool[count];
        scrollbars.Clear();
        scrollbarColors.Clear();
        for (var i = 0; i < count; i++)
        {
            var line = EditorHost.CmdLine(i);
            if (!lifted.TryGetValue(line, out var up))
            {
                up = Front(doc, line);
                lifted[line] = up;
            }
            layer[i] = up;
            if (line == 0 && EditorHost.CmdKind(i) == 1
                && (EditorHost.CmdX(i) >= PxW - ScrollbarColumn || EditorHost.CmdY(i) >= PxH - ScrollbarColumn))
            {
                skip[i] = true;
                scrollbars.Add(EditorHost.CmdX(i));
                scrollbars.Add(EditorHost.CmdY(i));
                scrollbars.Add(EditorHost.CmdW(i));
                scrollbars.Add(EditorHost.CmdH(i));
                scrollbarColors.Add(EditorHost.CmdColor(i));
            }
            // The band background is the bounding box of the band's commands (the scrollbar and size-less rects are not counted)
            if (line < 0 && EditorHost.CmdKind(i) != 3 && EditorHost.CmdW(i) > 0 && EditorHost.CmdH(i) > 0)
            {
                barX0 = System.Math.Min(barX0, EditorHost.CmdX(i));
                barY0 = System.Math.Min(barY0, EditorHost.CmdY(i));
                barX1 = System.Math.Max(barX1, EditorHost.CmdX(i) + EditorHost.CmdW(i));
                barY1 = System.Math.Max(barY1, EditorHost.CmdY(i) + EditorHost.CmdH(i));
            }
        }

        var changed = Changed(doc, layer, partial && barX1 <= barX0, first, last, lh);
        if (changed != null && !Banded(changed, lh))
            changed = null;
        if (changed != null)
        {
            RedrawnLines = changed.Count;
            if (changed.Count > 0 || shiftBy != 0)
                RenderLines(doc, text, face, front, back, layer, changed);
            return;
        }
        RedrawnLines = -1;

        Begin(face);
        Replay(text, layer, false, null);
        End();

        Begin(back);
        for (var line = first; line <= last; line++)
        {
            if (!doc.LineChanged(line - 1))
                continue;
            batch.Rect(x0 - 6, EditorHost.LineY(View, line) + 2, 4, lh - 4, Color.Rgb(1.0f, 0.45f, 0.45f, 0.8f));
        }
        foreach (var e in Errors)
        {
            if (e.Line + 1 < first || e.Line + 1 > last)
                continue;
            batch.Rect(x0 - 4, EditorHost.LineY(View, e.Line + 1), PxW - x0 + 4, lh, Color.Rgb(1.0f, 0.2f, 0.15f, 0.22f));
        }
        End();

        Begin(front);
        // Lifted lines and band background
        for (var line = first; line <= last; line++)
        {
            if (!doc.LineChanged(line - 1))
                continue;
            var s = doc.Text(line - 1);
            batch.Rect(x0 - 4, EditorHost.LineY(View, line), (Utf8.Cells(s, Utf8.Bytes(s)) + 1) * text.CellW + 8, lh,
                Color.Rgb(0.16f, 0.2f, 0.28f, 0.92f));
        }
        if (barX1 > barX0)
            batch.Rect(barX0, barY0, barX1 - barX0, barY1 - barY0, Color.Rgb(0.1f, 0.13f, 0.19f, 0.96f));
        Replay(text, layer, true, null);
        ErrorBar(text);
        End();
    }

    // Stores the commands as per-line lists, compares them with the previous ones, and returns the lines (line numbers) to redraw.
    // Line commands are compared by position from the line's top edge, so lines with the same content are not redrawn after scrolling (the texture shift amount is shiftBy).
    // For visible lines, even without commands, whether they differ from the version the game is running (layer and marks change) is put into the list.
    // Gutter line numbers (commands not tied to a line) are treated as belonging to the line whose height contains them.
    // null when bands alone cannot be redrawn (not partial, things not tied to lines changed, scroll is not an integer px or is large, too many lines to redraw)
    List<int>? Changed(Document doc, bool[] layer, bool partial, int first, int last, float lh)
    {
        var scroll = EditorHost.ScrollY(View);
        var hashes = new Dictionary<int, int>();
        var texts = new Dictionary<int, List<string>>();
        var tops = new Dictionary<int, float>();
        for (var line = first; line <= last; line++)
        {
            hashes[line] = doc.LineChanged(line - 1) ? 1 : 0;
            texts[line] = new List<string>();
            tops[line] = EditorHost.LineY(View, line);
        }
        var miscHash = 0;
        var miscTexts = new List<string>();
        for (var i = 0; i < count; i++)
        {
            if (skip[i])
                continue;
            var kind = EditorHost.CmdKind(i);
            var y = EditorHost.CmdY(i);
            var line = EditorHost.CmdLine(i);
            if (line == 0 && kind != 3 && lh > 0)
            {
                // The last line whose top edge is at most y (there are blank lines, so search instead of dividing by the line height)
                var at = first;
                for (var l = first + 1; l <= last && tops[l] <= y + 0.001f * lh; l++)
                    at = l;
                if (tops.TryGetValue(at, out var t) && y >= t - 0.01f && y + EditorHost.CmdH(i) <= t + lh + 0.01f)
                    line = at;
            }
            // Clips and size-less rects draw nothing by themselves, so they are not compared (their order changes with scrolling)
            if (line == 0 && (kind == 3 || (kind == 1 && (EditorHost.CmdW(i) <= 0 || EditorHost.CmdH(i) <= 0))))
                continue;
            var top = line > 0 && tops.TryGetValue(line, out var lineTop) ? lineTop : 0;
            var h = kind;
            h = h * 31 + (int)(EditorHost.CmdX(i) * 64);
            h = h * 31 + (int)((y - top) * 64);
            h = h * 31 + (int)(EditorHost.CmdW(i) * 64);
            h = h * 31 + (int)(EditorHost.CmdH(i) * 64);
            h = h * 31 + EditorHost.CmdColor(i);
            h = h * 31 + EditorHost.CmdFont(i);
            h = h * 31 + (layer[i] ? 1 : 0);
            if (line > 0 && hashes.TryGetValue(line, out var had))
            {
                hashes[line] = had * 1000003 + h;
                texts[line].Add(EditorHost.CmdText(i));
            }
            else
            {
                miscHash = miscHash * 1000003 + h;
                miscTexts.Add(EditorHost.CmdText(i));
            }
        }
        var beforeHashes = drawnHashes;
        var beforeTexts = drawnTexts;
        var same = miscHash == drawnMiscHash && Same(miscTexts, drawnMiscTexts);
        var dy = scroll - drawnScroll;
        drawnHashes = hashes;
        drawnTexts = texts;
        drawnMiscHash = miscHash;
        drawnMiscTexts = miscTexts;
        drawnScroll = scroll;
        shiftBy = 0;
        if (!partial || beforeHashes == null || beforeTexts == null || !same)
            return null;
        if (dy != System.Math.Floor(dy) || System.Math.Abs(dy) >= PxH * 0.5f)
            return null;
        var redraw = new List<int>();
        foreach (var pair in hashes)
        {
            // Lines that did not exist before, lines whose content changed, and lines over the edge revealed by shifting (previously outside the texture)
            var top = tops[pair.Key];
            var edge = dy > 0 ? top + lh > PxH - dy : (dy < 0 && top < -dy);
            if (edge || !beforeHashes.TryGetValue(pair.Key, out var was) || was != pair.Value || !Same(texts[pair.Key], beforeTexts[pair.Key]))
                redraw.Add(pair.Key);
        }
        var allowed = MaxPartialLines + (int)System.Math.Ceiling(System.Math.Abs(dy) / System.Math.Max(1, lh)) + 1;
        if (redraw.Count > allowed)
            return null;
        shiftBy = (int)dy;
        return redraw;
    }

    static bool Same(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

    // Whether every text command over a band fits within one band (text cannot be clipped by a band)
    bool Banded(List<int> changed, float lh)
    {
        foreach (var line in changed)
        {
            var top = EditorHost.LineY(View, line);
            for (var i = 0; i < count; i++)
            {
                if (EditorHost.CmdKind(i) != 2)
                    continue;
                var y = EditorHost.CmdY(i);
                var h = EditorHost.CmdH(i);
                if (y + h > top && y < top + lh && (y < top - 0.01f || y + h > top + lh + 0.01f))
                    return false;
            }
        }
        return true;
    }

    // Clears the changed lines' bands on the 3 layers and redraws only the commands over the bands and the line marks
    void RenderLines(Document doc, MonoText text, TextureRef face, TextureRef front, TextureRef back, bool[] layer, List<int> changed)
    {
        var lh = EditorHost.LineHeight(View);
        var x0 = EditorHost.TextX(View);
        if (shiftBy != 0)
        {
            Shift(face, shiftBy);
            Shift(back, shiftBy);
            Shift(front, shiftBy);
        }
        var bands = new List<float>();
        foreach (var line in changed)
            bands.Add(EditorHost.LineY(View, line));

        BeginLines(face, bands, lh);
        Replay(text, layer, false, bands);
        End();

        BeginLines(back, bands, lh);
        foreach (var line in changed)
            if (doc.LineChanged(line - 1))
                batch.Rect(x0 - 6, EditorHost.LineY(View, line) + 2, 4, lh - 4, Color.Rgb(1.0f, 0.45f, 0.45f, 0.8f));
        End();

        BeginLines(front, bands, lh);
        foreach (var line in changed)
        {
            if (!doc.LineChanged(line - 1))
                continue;
            var s = doc.Text(line - 1);
            batch.Rect(x0 - 4, EditorHost.LineY(View, line), (Utf8.Cells(s, Utf8.Bytes(s)) + 1) * text.CellW + 8, lh,
                Color.Rgb(0.16f, 0.2f, 0.28f, 0.92f));
        }
        Replay(text, layer, true, bands);
        End();
    }

    // Shader that copies a texture shifted up by dy px (transparent outside the source).
    // shift is (dy / PxH, fraction of the source height used). It samples pixel centers, so with integer px the pixels stay the same
    const string CopyVs = "struct Uniforms { float4 shift; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "struct VSOut { float2 uv : TEXCOORD0; float limit : TEXCOORD1; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  VSOut o;\n"
        + "  o.pos = float4(c.x * 2.0 - 1.0, 1.0 - c.y * 2.0, 0.0, 1.0);\n"
        + "  o.uv = float2(c.x, c.y + u.shift.x);\n"
        + "  o.limit = u.shift.y;\n"
        + "  return o;\n"
        + "}\n";
    const string CopyFs = "LUB_TEXTURE2D(src);\n"
        + "struct FSIn { float2 uv : TEXCOORD0; float limit : TEXCOORD1; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
        // WGSL does not allow sample inside a branch, so read first
        + "  float4 c = LUB_SAMPLE(src, i.uv);\n"
        + "  if (i.uv.y < 0.0 || i.uv.y >= i.limit) return float4(0, 0, 0, 0);\n"
        + "  return c;\n"
        + "}\n";
    static ShaderRef? copy;
    // Copy target for shifting (one per board, reused)
    TextureRef? scratch;

    // Shifts the contents of target up by dy px (copies shifted into scratch, then copies back)
    void Shift(TextureRef target, int dy)
    {
        if (copy == null || scratch == null)
            return;
        Copy(target, scratch, dy * 1.0f / PxH, 1);
        Copy(scratch, target, 0, 1);
    }

    void Copy(TextureRef src, TextureRef dst, float shift, float limit)
    {
        Gfx.BeginPass(new PassOpts { Target = dst, ClearColor = new float[] { 0, 0, 0, 0 } });
        var bindings = new Bindings();
        bindings.Set("src", src);
        bindings.Set("shift", new List<float> { shift, limit, 0, 0 });
        Gfx.Draw(4, bindings, new DrawOpts
        {
            Shader = copy,
            Cull = Gfx.Cull.None,
            Primitive = Gfx.Primitive.TriangleStrip,
            Depth = false,
        });
        Gfx.EndPass();
    }

    // Shader that paints bands transparent (SpriteBatch discards transparent parts, so it cannot be used for clearing).
    // rect is the clip coordinates of the band's top left and bottom right
    const string ClearVs = "struct Uniforms { float4 rect; };\n"
        + "ConstantBuffer<Uniforms> u;\n"
        + "[shader(\"vertex\")] float4 vs_main(uint vid : LUB_VERTEX_ID) : SV_Position {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  return float4(lerp(u.rect.x, u.rect.z, c.x), lerp(u.rect.y, u.rect.w, c.y), 0.0, 1.0);\n"
        + "}\n";
    const string ClearFs = "[shader(\"fragment\")] float4 fs_main() : SV_Target { return float4(0, 0, 0, 0); }\n";
    static ShaderRef? clear;

    // Declares the panel shaders. lider calls it every frame, even in frames without panels, so they are not created in the frame where a file is first opened
    public static void DeclareShaders()
    {
        clear = Gfx.UseShader("lider_panel_clear", ClearVs, ClearFs, 1);
        copy = Gfx.UseShader("lider_panel_copy", CopyVs, CopyFs, 1);
    }

    // Keeps the previous contents of target, makes bands transparent (top edge y of each band, height lh), then starts drawing
    void BeginLines(TextureRef target, List<float> bands, float lh)
    {
        Gfx.BeginPass(new PassOpts { Target = target, Load = Gfx.LoadAction.Load });
        if (clear != null)
            foreach (var y in bands)
            {
                var bindings = new Bindings();
                bindings.Set("rect", new List<float> { -1, 1 - y / PxH * 2, 1, 1 - (y + lh) / PxH * 2 });
                Gfx.Draw(4, bindings, new DrawOpts
                {
                    Shader = clear,
                    Cull = Gfx.Cull.None,
                    Primitive = Gfx.Primitive.TriangleStrip,
                    Depth = false,
                });
            }
        batch.Begin();
        bandH = lh;
    }

    float bandH;

    // Shows compile error messages (up to 3) in the band at the bottom of the panel as "line: message". If there are no errors, shows reload notices in yellow
    void ErrorBar(MonoText text)
    {
        var notice = Errors.Count == 0;
        var n = System.Math.Min(3, notice ? Notices.Count : Errors.Count);
        if (n == 0)
            return;
        var lh = text.LineH;
        var y0 = PxH - n * lh - 8;
        batch.Rect(0, y0, PxW, n * lh + 8, notice ? Color.Rgb(0.3f, 0.22f, 0.04f, 0.94f) : Color.Rgb(0.32f, 0.06f, 0.06f, 0.94f));
        for (var i = 0; i < n; i++)
        {
            var s = notice ? Notices[i] : (Errors[i].Line >= 0 ? (Errors[i].Line + 1) + ": " : "") + Errors[i].Message;
            text.Draw(batch, s, 8, y0 + 4 + i * lh, notice ? Color.Rgb(1.0f, 0.93f, 0.7f) : Color.Rgb(1.0f, 0.85f, 0.82f), 0, PxW - 8);
        }
    }

    // Draws the commands in the list that belong to the front layer (front) or to the surface. Clipping follows all commands.
    // If bands are given, draws only things over the bands (top edge y, height bandH), clipped to the bands
    void Replay(MonoText text, bool[] layer, bool front, List<float>? bands)
    {
        clipX = 0;
        clipY = 0;
        clipW = PxW;
        clipH = PxH;
        for (var i = 0; i < count; i++)
        {
            var kind = EditorHost.CmdKind(i);
            var x = EditorHost.CmdX(i);
            var y = EditorHost.CmdY(i);
            var w = EditorHost.CmdW(i);
            var h = EditorHost.CmdH(i);
            if (kind == 3)
            {
                clipX = x;
                clipY = y;
                clipW = w;
                clipH = h;
                continue;
            }
            if (layer[i] != front || skip[i])
                continue;
            var color = Unpack(EditorHost.CmdColor(i));
            if (bands == null)
            {
                Draw(text, kind, i, x, y, w, h, color, clipY, clipY + clipH);
                continue;
            }
            foreach (var top in bands)
            {
                var y0 = System.Math.Max(clipY, top);
                var y1 = System.Math.Min(clipY + clipH, top + bandH);
                if (y + h > y0 && y < y1)
                    Draw(text, kind, i, x, y, w, h, color, y0, y1);
            }
        }
    }

    // Draws command i (kind 1 is rect, 2 is text), clipped to the current clip in x and to y0..y1 in y
    void Draw(MonoText text, int kind, int i, float x, float y, float w, float h, Color color, float y0, float y1)
    {
        if (kind == 1)
        {
            var x1 = System.Math.Max(x, clipX);
            var top = System.Math.Max(y, y0);
            var x2 = System.Math.Min(x + w, clipX + clipW);
            var bottom = System.Math.Min(y + h, y1);
            if (x2 > x1 && bottom > top)
                batch.Rect(x1, top, x2 - x1, bottom - top, color);
        }
        else if (kind == 2 && !Icon(EditorHost.CmdFont(i)) && y + h > y0 && y < y1)
        {
            text.Draw(batch, EditorHost.CmdText(i), x, y, color, clipX, clipX + clipW);
        }
    }

    bool Icon(int font)
    {
        if (!icon.TryGetValue(font, out var yes))
        {
            yes = EditorHost.FontName(font) == "icons.ttf";
            icon[font] = yes;
        }
        return yes;
    }

    // Color packed as RGBA into 32 bits (R in the top 8 bits)
    static Color Unpack(int c)
    {
        return Color.Rgb(((c >> 24) & 255) / 255.0f, ((c >> 16) & 255) / 255.0f, ((c >> 8) & 255) / 255.0f, (c & 255) / 255.0f);
    }
}
