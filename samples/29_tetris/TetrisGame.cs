using System;

public class TetrisGame
{
    public int[] Board = new int[200];
    public int Piece, Next, Rotation, X, Y, Score, Lines;
    public bool Over, Paused;
    public int Locked;
    private int seed = 917;
    private int[] bag = new int[7];
    private int bagIndex = 7;
    private float fall;
    private static int[] cells = new int[] {
        4, 5, 6, 7, 1, 2, 5, 6, 1, 4, 5, 6,
        1, 2, 4, 5, 0, 1, 5, 6, 0, 4, 5, 6, 2, 4, 5, 6
    };

    public TetrisGame() { Reset(); }

    public void Reset()
    {
        Board = new int[200];
        // TinyC# sized arrays become Lua tables; initialize occupied-cell values explicitly.
        for (int i = 0; i < 200; i++) Board[i] = 0;
        Score = 0; Lines = 0; Locked = 0; Over = false; Paused = false;
        seed = 917; bagIndex = 7;
        Next = Take(); Spawn();
    }

    private int Take()
    {
        if (bagIndex == 7)
        {
            for (int i = 0; i < 7; i++) bag[i] = i;
            for (int i = 6; i > 0; i--)
            {
                seed = (seed * 65 + 17) % 65521;
                int j = seed % (i + 1);
                int value = bag[i]; bag[i] = bag[j]; bag[j] = value;
            }
            bagIndex = 0;
        }
        int result = bag[bagIndex];
        bagIndex++;
        return result;
    }

    public int Cell(int piece, int rotation, int index)
    {
        int cell = cells[piece * 4 + index];
        int x = cell % 4;
        int y = (int)Math.Floor(cell / 4.0f);
        int size = piece == 0 ? 4 : 3;
        if (piece != 1)
            for (int r = 0; r < rotation; r++)
            { int oldX = x; x = size - 1 - y; y = oldX; }
        return y * 4 + x;
    }

    public bool Fits(int x, int y, int rotation)
    {
        for (int i = 0; i < 4; i++)
        {
            int c = Cell(Piece, rotation, i);
            int bx = x + c % 4, by = y + (int)Math.Floor(c / 4.0f);
            if (bx < 0 || bx >= 10 || by < 0 || by >= 20) return false;
            if (Board[by * 10 + bx] != 0) return false;
        }
        return true;
    }

    public void Spawn()
    {
        Piece = Next; Next = Take(); Rotation = 0; X = 3; Y = 0; fall = 0;
        Over = !Fits(X, Y, Rotation);
    }

    public bool Move(int dx, int dy)
    {
        if (Over || Paused || !Fits(X + dx, Y + dy, Rotation)) return false;
        X += dx; Y += dy; return true;
    }

    public void Rotate(int direction)
    {
        if (Over || Paused) return;
        int r = (Rotation + direction + 4) % 4;
        // Small horizontal wall kicks; this sample does not implement SRS.
        int[] kicks = new int[] { 0, -1, 1, -2, 2 };
        foreach (int dx in kicks)
            if (Fits(X + dx, Y, r)) { X += dx; Rotation = r; return; }
    }

    public int GhostY()
    {
        int y = Y;
        while (Fits(X, y + 1, Rotation)) y++;
        return y;
    }

    public void Drop()
    {
        if (Over || Paused) return;
        int target = GhostY(); Score += (target - Y) * 2; Y = target; Lock();
    }

    public void Tick(float dt, bool soft)
    {
        if (Over || Paused) return;
        fall += dt;
        float interval = soft ? 0.045f : Math.Max(0.10f, 0.7f - Level() * 0.055f);
        while (fall >= interval)
        {
            fall -= interval;
            if (!Move(0, 1)) { Lock(); break; }
            if (soft) Score++;
        }
    }

    public int Level() { return (int)Math.Floor(Lines / 10.0f); }

    private void Lock()
    {
        for (int i = 0; i < 4; i++)
        {
            int c = Cell(Piece, Rotation, i);
            Board[(Y + (int)Math.Floor(c / 4.0f)) * 10 + X + c % 4] = Piece + 1;
        }
        Locked++;
        int cleared = ClearLines();
        int[] points = new int[] { 0, 100, 300, 500, 800 };
        Score += points[cleared] * (Level() + 1);
        Lines += cleared;
        Spawn();
    }

    public int ClearLines()
    {
        int target = 19, cleared = 0;
        for (int row = 19; row >= 0; row--)
        {
            bool full = true;
            for (int x = 0; x < 10; x++) if (Board[row * 10 + x] == 0) full = false;
            if (full) { cleared++; continue; }
            for (int x = 0; x < 10; x++) Board[target * 10 + x] = Board[row * 10 + x];
            target--;
        }
        for (int row = target; row >= 0; row--)
            for (int x = 0; x < 10; x++) Board[row * 10 + x] = 0;
        return cleared;
    }
}
