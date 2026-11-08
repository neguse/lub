using System.Collections.Generic;

namespace TcsSelf;

// .NET の ArraySortHelper<TKey, TValue> と同じ introsort (key と並びを一緒に並べる) と二分探索。
// enum の名前の表を .NET の EnumInfo と同じ並びにするのに使う。key は符号なしで比べる
public static class EnumSort
{
    public static void IntrospectiveSort(List<int> keys, List<int> values)
    {
        int n = keys.Count;
        if (n > 1) IntroSort(keys, values, 0, n, 2 * (Log2(n) + 1));
    }

    private static int Log2(int v)
    {
        int r = 0;
        while (v > 1)
        {
            v = v >> 1;
            r++;
        }
        return r;
    }

    // 符号なしの 32 bit として比べる (C# の enum の並びは underlying の値を符号なしで比べる)
    public static bool UnsignedLess(int a, int b) => (a ^ (-2147483647 - 1)) < (b ^ (-2147483647 - 1));

    private static bool Less(int a, int b) => UnsignedLess(a, b);

    private static void Swap(List<int> keys, List<int> values, int i, int j)
    {
        int k = keys[i];
        keys[i] = keys[j];
        keys[j] = k;
        int v = values[i];
        values[i] = values[j];
        values[j] = v;
    }

    private static void SwapIfGreater(List<int> keys, List<int> values, int i, int j)
    {
        if (Less(keys[j], keys[i])) Swap(keys, values, i, j);
    }

    // [lo, lo + size) を並べる
    private static void IntroSort(List<int> keys, List<int> values, int lo, int size, int depthLimit)
    {
        int partitionSize = size;
        while (partitionSize > 1)
        {
            if (partitionSize <= 16)
            {
                if (partitionSize == 2)
                {
                    SwapIfGreater(keys, values, lo, lo + 1);
                    return;
                }
                if (partitionSize == 3)
                {
                    SwapIfGreater(keys, values, lo, lo + 1);
                    SwapIfGreater(keys, values, lo, lo + 2);
                    SwapIfGreater(keys, values, lo + 1, lo + 2);
                    return;
                }
                InsertionSort(keys, values, lo, partitionSize);
                return;
            }
            if (depthLimit == 0)
            {
                HeapSort(keys, values, lo, partitionSize);
                return;
            }
            depthLimit--;
            int p = PickPivotAndPartition(keys, values, lo, partitionSize);
            IntroSort(keys, values, lo + p + 1, partitionSize - (p + 1), depthLimit);
            partitionSize = p;
        }
    }

    private static int PickPivotAndPartition(List<int> keys, List<int> values, int lo, int size)
    {
        int hi = size - 1;
        int middle = hi >> 1;
        SwapIfGreater(keys, values, lo, lo + middle);
        SwapIfGreater(keys, values, lo, lo + hi);
        SwapIfGreater(keys, values, lo + middle, lo + hi);
        int pivot = keys[lo + middle];
        Swap(keys, values, lo + middle, lo + hi - 1);
        int left = 0;
        int right = hi - 1;
        while (left < right)
        {
            left++;
            while (Less(keys[lo + left], pivot)) left++;
            right--;
            while (Less(pivot, keys[lo + right])) right--;
            if (left >= right) break;
            Swap(keys, values, lo + left, lo + right);
        }
        if (left != hi - 1) Swap(keys, values, lo + left, lo + hi - 1);
        return left;
    }

    private static void InsertionSort(List<int> keys, List<int> values, int lo, int size)
    {
        for (int i = 0; i < size - 1; i++)
        {
            int t = keys[lo + i + 1];
            int tv = values[lo + i + 1];
            int j = i;
            while (j >= 0 && Less(t, keys[lo + j]))
            {
                keys[lo + j + 1] = keys[lo + j];
                values[lo + j + 1] = values[lo + j];
                j--;
            }
            keys[lo + j + 1] = t;
            values[lo + j + 1] = tv;
        }
    }

    private static void HeapSort(List<int> keys, List<int> values, int lo, int n)
    {
        for (int i = n >> 1; i >= 1; i--) DownHeap(keys, values, lo, i, n);
        for (int i = n; i > 1; i--)
        {
            Swap(keys, values, lo, lo + i - 1);
            DownHeap(keys, values, lo, 1, i - 1);
        }
    }

    private static void DownHeap(List<int> keys, List<int> values, int lo, int i, int n)
    {
        int d = keys[lo + i - 1];
        int dv = values[lo + i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && Less(keys[lo + child - 1], keys[lo + child])) child++;
            if (!Less(d, keys[lo + child - 1])) break;
            keys[lo + i - 1] = keys[lo + child - 1];
            values[lo + i - 1] = values[lo + child - 1];
            i = child;
        }
        keys[lo + i - 1] = d;
        values[lo + i - 1] = dv;
    }

    // .NET の span の BinarySearch と同じ手順 (同じ値が複数あればどれかの位置)
    public static int BinarySearch(List<int> keys, int value)
    {
        int lo = 0;
        int hi = keys.Count - 1;
        while (lo <= hi)
        {
            int i = lo + ((hi - lo) >> 1);
            int k = keys[i];
            if (k == value) return i;
            if (Less(k, value)) lo = i + 1;
            else hi = i - 1;
        }
        return ~lo;
    }
}
