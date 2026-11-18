namespace TinyCs.Tcs2c.Tests;

// A break of a loop inside a switch section leaves only that loop, and the rest of the switch runs.
// continue goes to the next iteration of the loop enclosing the switch.
// It is the same for a switch with a break in the middle of a section (IlBreakScope in IL).
// The expected value is the stdout of the same program run on CoreCLR
public class SwitchLoopBreakDifferentialTests
{
    [CFact]
    public void Switch_LoopBreakInsideSection_LeavesOnlyTheLoop()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                static void Run(int value, bool stop)
                {
                    switch (value)
                    {
                        case 1:
                            if (stop) break;
                            while (true)
                            {
                                Console.WriteLine("loop");
                                break;
                            }
                            Console.WriteLine("case");
                            break;
                    }
                    Console.WriteLine("end");
                }

                // The break of each kind of loop inside a switch with a break in the middle of a section
                static string Loops(int kind, bool stop)
                {
                    var log = kind + ":";
                    var array = new int[] { 1, 2, 3 };
                    var list = new List<int> { 4, 5, 6 };
                    var dict = new Dictionary<int, int> { [7] = 1, [8] = 2, [9] = 3 };
                    var set = new HashSet<int> { 10, 11, 12 };
                    var queue = new Queue<int>();
                    queue.Enqueue(13);
                    queue.Enqueue(14);
                    switch (kind)
                    {
                        case 0:
                            if (stop) break;
                            var i = 0;
                            while (i < 10)
                            {
                                i++;
                                if (i == 2) break;
                            }
                            log += "while" + i;
                            break;
                        case 1:
                            if (stop) break;
                            var j = 0;
                            do
                            {
                                j++;
                                if (j == 3) break;
                            } while (j < 10);
                            log += "do" + j;
                            break;
                        case 2:
                            if (stop) break;
                            for (var n = 0; n < 10; n++)
                            {
                                if (n == 4)
                                {
                                    log += "for" + n;
                                    break;
                                }
                            }
                            log += ",";
                            break;
                        case 3:
                            if (stop) break;
                            for (var n = 0; n < 10; n += 3)
                            {
                                log += n;
                                if (n == 3) break;
                            }
                            log += "step";
                            break;
                        case 4:
                            if (stop) break;
                            foreach (var x in array)
                            {
                                log += x;
                                if (x == 2) break;
                            }
                            log += "array";
                            break;
                        case 5:
                            if (stop) break;
                            foreach (var x in list)
                            {
                                log += x;
                                if (x == 5) break;
                            }
                            log += "list";
                            break;
                        case 6:
                            if (stop) break;
                            foreach (var kv in dict)
                            {
                                log += kv.Key;
                                if (kv.Value == 2) break;
                            }
                            log += "dict";
                            break;
                        case 7:
                            if (stop) break;
                            foreach (var x in set)
                            {
                                log += x;
                                if (x == 11) break;
                            }
                            log += "set";
                            break;
                        case 8:
                            if (stop) break;
                            foreach (var x in queue)
                            {
                                log += x;
                                break;
                            }
                            log += "queue";
                            break;
                        case 9:
                            if (stop) break;
                            foreach (var r in "abc".EnumerateRunes())
                            {
                                log += r;
                                if (r.Value == 'b') break;
                            }
                            log += "runes";
                            break;
                        default:
                            if (stop) break;
                            while (true) break;
                            log += "default";
                            break;
                    }
                    return log + "|";
                }

                // A switch inside a for inside a switch: a break of the inner switch leaves the inner switch, a break of the for leaves the for, and continue goes to the next iteration of the for
                static string Nested(bool stop)
                {
                    var log = "";
                    switch (stop ? 0 : 1)
                    {
                        case 1:
                            if (stop) break;
                            for (var n = 0; n < 8; n++)
                            {
                                switch (n)
                                {
                                    case 1:
                                        if (stop) break;
                                        log += "one";
                                        break;
                                    case 3:
                                        continue;
                                    case 5:
                                        log += "five";
                                        break;
                                }
                                log += n;
                                if (n == 5) break;
                            }
                            log += "after";
                            break;
                    }
                    return log;
                }

                // continue and break in a section of a switch inside a loop
                static string ContinueInSwitch()
                {
                    var log = "";
                    var i = 0;
                    while (i < 6)
                    {
                        i++;
                        switch (i % 3)
                        {
                            case 0:
                                if (i == 3) break;
                                log += "z";
                                continue;
                            case 1:
                                log += "o";
                                break;
                        }
                        log += i;
                    }
                    return log;
                }

                // A loop inside a switch inside a loop: after a break of the inner loop, the rest of the switch and the outer loop continue
                static string LoopSwitchLoop()
                {
                    var log = "";
                    for (var a = 0; a < 3; a++)
                    {
                        switch (a)
                        {
                            case 1:
                                if (a > 5) break;
                                var b = 0;
                                while (true)
                                {
                                    b++;
                                    if (b == 2) break;
                                }
                                log += "b" + b;
                                break;
                            default:
                                log += "d";
                                break;
                        }
                        log += a;
                    }
                    return log;
                }

                public static void Main()
                {
                    Run(1, false);
                    Run(1, true);
                    var line = "";
                    for (var k = 0; k <= 10; k++) line += Loops(k, false);
                    Console.WriteLine(line);
                    line = "";
                    for (var k = 0; k <= 10; k++) line += Loops(k, true);
                    Console.WriteLine(line);
                    Console.WriteLine(Nested(false) + "|" + Nested(true));
                    Console.WriteLine(ContinueInSwitch());
                    Console.WriteLine(LoopSwitchLoop());
                }
            }
            """, "P");
        Assert.Equal("""
            loop
            case
            end
            end
            0:while2|1:do3|2:for4,|3:03step|4:12array|5:45list|6:78dict|7:1011set|8:13queue|9:abrunes|10:default|
            0:|1:|2:|3:|4:|5:|6:|7:|8:|9:|10:|
            0one124five5after|
            o123o45z
            d0b21d2
            """.Replace("\r\n", "\n"), output);
    }
}
