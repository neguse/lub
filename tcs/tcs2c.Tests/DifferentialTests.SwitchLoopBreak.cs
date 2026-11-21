namespace TinyCs.Tcs2c.Tests;

// switch の section の中の loop の break はその loop だけを抜け、switch の続きを実行する。
// continue は switch を囲む loop の次の周へ進む。
// section の途中に break がある switch (IL の IlBreakScope) でも同じ。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
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

                // section の途中に break がある switch の中の、それぞれの loop の break
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

                // switch の中の for の中の switch: 内側の switch の break は内側の switch を、for の break は for を抜け、continue は for の次の周へ進む
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

                // loop の中の switch の section の continue と break
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

                // loop の中の switch の中の loop: 内側の loop の break の後も、switch の続きと外側の loop が続く
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
