// tcs の Dictionary / HashSet の仕様 (support-matrix §18: 列挙は挿入順) を .NET で動かすときの実装。
// TinySystem.props がこの file を .NET の project に source で入れ、BCL の System.Collections.Generic.Dictionary /
// HashSet を同じ名前のこの型で隠す (source の型が参照の型より優先。CS0436 は props が抑える)。tcs の変換 (Lua / C / VM)
// はこの file を使わない (それぞれの runtime が同じ仕様を持つ)。
//
// 仕様:
// - 列挙 (foreach、Keys、Values) は、今入っている key を最初に入れた順。上書きは位置を変えない。
//   Remove した key をもう一度足すと末尾
// - Dictionary の foreach の途中で新しい key を足すか Clear すると InvalidOperationException (tcs の fault と同じ扱い)。
//   上書きと Remove は許し、消した key はその後の列挙に出ない
// - HashSet の foreach は始めた時点の要素を挿入順に回る (途中の変更は列挙に影響しない。tcs の lowering と同じ)
#nullable enable
#pragma warning disable CS0436
// .NET の実装なので、ゲームの project に取り込んだときに TinyC# の analyzer の診断を当てない
#pragma warning disable TCS1001, TCS1002, TCS1003, TCS1004

namespace System.Collections.Generic
{
    public class Dictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue>>, IReadOnlyDictionary<TKey, TValue>
        where TKey : notnull
    {
        private readonly IEqualityComparer<TKey> _comparer;
        // entry は挿入順に積む。消した entry は live = false の穴で、広げるときに詰める
        private TKey[] _keys;
        private TValue[] _values;
        private int[] _hashes;
        private int[] _next;   // 同じ bucket の次の entry (-1 は末尾)
        private bool[] _live;
        private int[] _buckets; // bucket → entry + 1 (0 は空)
        private int _used;
        private int _count;
        private int _version;

        public Dictionary() : this(0, null) { }
        public Dictionary(int capacity) : this(capacity, null) { }
        public Dictionary(IEqualityComparer<TKey>? comparer) : this(0, comparer) { }

        public Dictionary(int capacity, IEqualityComparer<TKey>? comparer)
        {
            _comparer = comparer ?? EqualityComparer<TKey>.Default;
            var cap = Math.Max(capacity, 4);
            _keys = new TKey[cap];
            _values = new TValue[cap];
            _hashes = new int[cap];
            _next = new int[cap];
            _live = new bool[cap];
            _buckets = new int[BucketCount(cap)];
        }

        public Dictionary(IEnumerable<KeyValuePair<TKey, TValue>> source) : this(0, null)
        {
            foreach (var kv in source) Add(kv.Key, kv.Value);
        }

        private static int BucketCount(int cap)
        {
            var n = 8;
            while (n < cap * 2) n *= 2;
            return n;
        }

        public int Count => _count;

        public TValue this[TKey key]
        {
            get
            {
                var at = Find(key);
                if (at < 0) throw new KeyNotFoundException($"The given key '{key}' was not present in the dictionary.");
                return _values[at];
            }
            set
            {
                var at = Find(key);
                if (at >= 0) _values[at] = value;
                else Insert(key, value);
            }
        }

        public KeyCollection Keys => new(this);
        public ValueCollection Values => new(this);
        IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;
        IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

        public void Add(TKey key, TValue value)
        {
            if (Find(key) >= 0) throw new ArgumentException($"An item with the same key has already been added. Key: {key}");
            Insert(key, value);
        }

        public bool TryAdd(TKey key, TValue value)
        {
            if (Find(key) >= 0) return false;
            Insert(key, value);
            return true;
        }

        public bool ContainsKey(TKey key) => Find(key) >= 0;

        public bool ContainsValue(TValue value)
        {
            var eq = EqualityComparer<TValue>.Default;
            for (var i = 0; i < _used; i++)
                if (_live[i] && eq.Equals(_values[i], value)) return true;
            return false;
        }

        public bool TryGetValue(TKey key, out TValue value)
        {
            var at = Find(key);
            value = at >= 0 ? _values[at] : default!;
            return at >= 0;
        }

        public bool Remove(TKey key) => Remove(key, out _);

        public bool Remove(TKey key, out TValue value)
        {
            ArgumentNullException.ThrowIfNull(key);
            var hash = _comparer.GetHashCode(key) & 0x7FFFFFFF;
            var b = hash & (_buckets.Length - 1);
            var prev = -1;
            for (var i = _buckets[b] - 1; i >= 0; prev = i, i = _next[i])
            {
                if (_hashes[i] != hash || !_comparer.Equals(_keys[i], key)) continue;
                if (prev < 0) _buckets[b] = _next[i] + 1;
                else _next[prev] = _next[i];
                value = _values[i];
                _live[i] = false;
                _keys[i] = default!;
                _values[i] = default!;
                _count--;
                return true;
            }
            value = default!;
            return false;
        }

        public void Clear()
        {
            Array.Clear(_keys);
            Array.Clear(_values);
            Array.Clear(_live);
            Array.Clear(_buckets);
            _used = 0;
            _count = 0;
            _version++;
        }

        private int Find(TKey key)
        {
            ArgumentNullException.ThrowIfNull(key);
            var hash = _comparer.GetHashCode(key) & 0x7FFFFFFF;
            for (var i = _buckets[hash & (_buckets.Length - 1)] - 1; i >= 0; i = _next[i])
                if (_hashes[i] == hash && _comparer.Equals(_keys[i], key)) return i;
            return -1;
        }

        private void Insert(TKey key, TValue value)
        {
            if (_used == _keys.Length) Rebuild(Math.Max(_count * 2, 4));
            var hash = _comparer.GetHashCode(key) & 0x7FFFFFFF;
            var at = _used++;
            _keys[at] = key;
            _values[at] = value;
            _hashes[at] = hash;
            _live[at] = true;
            var b = hash & (_buckets.Length - 1);
            _next[at] = _buckets[b] - 1;
            _buckets[b] = at + 1;
            _count++;
            _version++;
        }

        // 穴を詰めて (並びは保つ) cap に広げる
        private void Rebuild(int cap)
        {
            var keys = new TKey[cap];
            var values = new TValue[cap];
            var hashes = new int[cap];
            var next = new int[cap];
            var live = new bool[cap];
            var buckets = new int[BucketCount(cap)];
            var n = 0;
            for (var i = 0; i < _used; i++)
            {
                if (!_live[i]) continue;
                keys[n] = _keys[i];
                values[n] = _values[i];
                hashes[n] = _hashes[i];
                live[n] = true;
                var b = hashes[n] & (buckets.Length - 1);
                next[n] = buckets[b] - 1;
                buckets[b] = n + 1;
                n++;
            }
            (_keys, _values, _hashes, _next, _live, _buckets, _used) = (keys, values, hashes, next, live, buckets, n);
        }

        public Enumerator GetEnumerator() => new(this);
        IEnumerator<KeyValuePair<TKey, TValue>> IEnumerable<KeyValuePair<TKey, TValue>>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public struct Enumerator : IEnumerator<KeyValuePair<TKey, TValue>>
        {
            private readonly Dictionary<TKey, TValue> _d;
            private readonly int _version;
            private int _i;

            internal Enumerator(Dictionary<TKey, TValue> d)
            {
                _d = d;
                _version = d._version;
                _i = -1;
            }

            public KeyValuePair<TKey, TValue> Current => new(_d._keys[_i], _d._values[_i]);
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_d._version != _version)
                    throw new InvalidOperationException("Collection was modified; enumeration operation may not execute.");
                while (++_i < _d._used)
                    if (_d._live[_i]) return true;
                return false;
            }

            public void Reset() => _i = -1;
            public void Dispose() { }
        }

        public sealed class KeyCollection : IReadOnlyCollection<TKey>
        {
            private readonly Dictionary<TKey, TValue> _d;
            internal KeyCollection(Dictionary<TKey, TValue> d) => _d = d;
            public int Count => _d.Count;

            public IEnumerator<TKey> GetEnumerator()
            {
                foreach (var kv in _d) yield return kv.Key;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }

        public sealed class ValueCollection : IReadOnlyCollection<TValue>
        {
            private readonly Dictionary<TKey, TValue> _d;
            internal ValueCollection(Dictionary<TKey, TValue> d) => _d = d;
            public int Count => _d.Count;

            public IEnumerator<TValue> GetEnumerator()
            {
                foreach (var kv in _d) yield return kv.Value;
            }

            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        }
    }

    public class HashSet<T> : IReadOnlyCollection<T>
    {
        // 値は Dictionary の key (挿入順と穴の詰め方が同じ)。HashSet は null も要素にできるので key を包む
        private readonly struct Slot(T value)
        {
            public readonly T Value = value;
        }

        private sealed class SlotComparer(IEqualityComparer<T> inner) : IEqualityComparer<Slot>
        {
            public bool Equals(Slot a, Slot b) => inner.Equals(a.Value, b.Value);
            public int GetHashCode(Slot s) => s.Value is null ? 0 : inner.GetHashCode(s.Value);
        }

        private readonly Dictionary<Slot, bool> _items;

        public HashSet() : this((IEqualityComparer<T>?)null) { }

        public HashSet(IEqualityComparer<T>? comparer) =>
            _items = new Dictionary<Slot, bool>(new SlotComparer(comparer ?? EqualityComparer<T>.Default));

        public HashSet(IEnumerable<T> source) : this()
        {
            foreach (var x in source) Add(x);
        }

        public int Count => _items.Count;
        public bool Add(T item) => _items.TryAdd(new Slot(item), true);
        public bool Remove(T item) => _items.Remove(new Slot(item));
        public bool Contains(T item) => _items.ContainsKey(new Slot(item));
        public void Clear() => _items.Clear();

        public void UnionWith(IEnumerable<T> other)
        {
            foreach (var x in other) Add(x);
        }

        public void ExceptWith(IEnumerable<T> other)
        {
            foreach (var x in other) Remove(x);
        }

        public void IntersectWith(IEnumerable<T> other)
        {
            var keep = new HashSet<T>(other);
            foreach (var x in ToList())
                if (!keep.Contains(x)) Remove(x);
        }

        public bool Overlaps(IEnumerable<T> other)
        {
            foreach (var x in other)
                if (Contains(x)) return true;
            return false;
        }

        public bool SetEquals(IEnumerable<T> other)
        {
            var o = new HashSet<T>(other);
            if (o.Count != Count) return false;
            foreach (var x in this)
                if (!o.Contains(x)) return false;
            return true;
        }

        private List<T> ToList()
        {
            var list = new List<T>(Count);
            foreach (var kv in _items) list.Add(kv.Key.Value);
            return list;
        }

        // 始めた時点の要素を挿入順に回る (途中の変更は列挙に影響しない)
        public List<T>.Enumerator GetEnumerator() => ToList().GetEnumerator();
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
