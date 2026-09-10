using System;
using System.Collections.Generic;

static class Program
{
    static int checks;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        checks++;
    }

    static void Main()
    {
        var g = new TetrisGame();
        var seen = new HashSet<int>();
        for (int i = 0; i < 7; i++) { seen.Add(g.Piece); g.Spawn(); }
        Check(seen.Count == 7, "first bag contains all seven pieces");
        for (int piece = 0; piece < 7; piece++)
        {
            g.Reset(); g.Piece = piece;
            for (int r = 0; r < 4; r++)
            {
                var cells = new HashSet<int>();
                for (int i = 0; i < 4; i++) cells.Add(g.Cell(piece, r, i));
                Check(cells.Count == 4, "rotation preserves four distinct cells");
                Check(g.Fits(3, 0, r), "rotation fits at spawn");
            }
            while (g.Move(-1, 0)) { }
            Check(!g.Move(-1, 0), "left wall blocks movement");
            g.Rotate(1);
            Check(g.Fits(g.X, g.Y, g.Rotation), "wall rotation stays legal");
            g.Drop();
            int occupied = 0;
            foreach (int cell in g.Board) if (cell != 0) occupied++;
            Check(occupied == 4 && g.Locked == 1, "hard drop locks exactly four cells");
        }
        g.Reset();
        for (int y = 16; y < 20; y++)
            for (int x = 0; x < 10; x++) g.Board[y * 10 + x] = x == 5 ? 0 : 2;
        g.Piece = 0; g.X = 3; g.Y = 0; g.Rotation = 0;
        g.Rotate(1); g.Drop();
        Check(g.Lines == 4 && g.Score == 832, "vertical I clears four rows and scores drop distance");
        Check(Array.TrueForAll(g.Board, c => c == 0), "four-line clear empties fixture");
        g.Reset(); g.Board[180] = 3;
        for (int x = 0; x < 10; x++) g.Board[190 + x] = 2;
        Check(g.ClearLines() == 1 && g.Board[190] == 3 && g.Board[180] == 0, "row above falls into cleared row");
        g.Reset(); int before = g.Y;
        g.Paused = true; g.Tick(2, true); g.Drop(); g.Move(1, 0); g.Rotate(1);
        Check(g.Y == before && g.Locked == 0 && g.X == 3 && g.Rotation == 0, "pause blocks gameplay");
        g.Paused = false; g.Tick(0.71f, false);
        Check(g.Y == before + 1, "gravity advances one row");
        g.Reset();
        for (int i = 0; i < 40; i++) g.Board[i] = 1;
        g.Spawn(); Check(g.Over, "occupied spawn causes game over");
        g.Drop(); Check(g.Locked == 0, "game over rejects drop");
        g.Reset(); Check(!g.Over && g.Lines == 0 && g.Score == 0, "restart clears game over and score");
        var other = new TetrisGame();
        for (int i = 0; i < 100; i++)
        {
            Check(g.Piece == other.Piece, "reset reproduces sequence");
            g.Spawn(); other.Spawn();
        }
        Console.WriteLine("TETRIS_TESTS passed=" + checks);
    }
}
