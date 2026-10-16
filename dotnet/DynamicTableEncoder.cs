using System.Net.Http.QPack;

// Minimal QPACK encoder with a dynamic table (RFC 9204), no Huffman, one instance per connection.
//
// Safe mode (SETTINGS_QPACK_BLOCKED_STREAMS = 0): field lines only reference entries whose insertion the
// peer has acknowledged, so no request can ever block. Acknowledgements are modelled as arriving
// `ackDelay` requests after the request that produced them, with in-order delivery.
//
// Policy: a header is inserted when its value repeats one of the last 16 values seen for that name (one-off
// values such as traceparent never are); the oldest entry is evicted only when its insertion is acknowledged
// and no unacknowledged section uses it.
//
// Sensitive fields (authorization, cookies under 20 chars) are never indexed and are sent with the N bit,
// like nghttp3, because the dynamic table lets an attacker who can add headers probe values (RFC 9204 7.1).
// indexSensitive: true indexes them like any other header (QUICHE and ls-qpack behaviour).
//
// Lookups are keyed by NAME and values are only compared, never hashed: hashing a 1 KB token on every
// request made a first version (git history) 7x slower than today's encoding.
public sealed class DynamicTableEncoder
{
    private const int RecentValues = 16;

    private sealed class NameState(string name)
    {
        public string Name { get; } = name;
        public List<Entry> Entries { get; } = new(2); // live dynamic entries with this name, oldest first
        public string?[] Recent { get; } = new string?[RecentValues]; // values seen but not inserted
        public int RecentNext { get; set; }
    }

    private sealed class Entry(NameState state, string value, int absoluteIndex)
    {
        public NameState State { get; } = state;
        public string Name => State.Name;
        public string Value { get; } = value;
        public int AbsoluteIndex { get; } = absoluteIndex;
        public int Size { get; } = state.Name.Length + value.Length + 32;
        public int UnacknowledgedReferences { get; set; }
    }

    private readonly record struct PendingSection(int Section, int InsertCountAfter, List<Entry> References);

    private readonly int _capacity;
    private readonly int _maxEntries;
    private readonly int _ackDelay;
    private readonly bool _indexSensitive;
    private readonly StaticLookup _static = StaticLookup.Instance;

    private readonly Queue<Entry> _table = new();
    private readonly Dictionary<string, NameState> _names = new();
    private readonly Queue<PendingSection> _pending = new();
    private readonly Stack<List<Entry>> _listPool = new();

    private readonly byte[] _encoderStream = new byte[64 * 1024];
    private readonly byte[] _lines = new byte[64 * 1024];
    private readonly byte[] _block = new byte[64 * 1024];

    private int _insertCount;
    private int _knownReceivedCount;
    private int _tableSize;
    private int _section;
    private bool _capacitySent;

    public DynamicTableEncoder(int capacity, int ackDelay, bool indexSensitive = false)
    {
        _capacity = capacity;
        _maxEntries = capacity / 32;
        _ackDelay = ackDelay;
        _indexSensitive = indexSensitive;
    }

    public ReadOnlySpan<byte> EncoderStream => _encoderStream;
    public ReadOnlySpan<byte> Block => _block;

    // Encodes one header list; returns the bytes written to EncoderStream and Block.
    public (int EncoderLength, int BlockLength) Encode(List<(string Name, string Value)> headers)
    {
        ApplyAcknowledgements();

        var encoderLength = 0;
        if (!_capacitySent)
        {
            encoderLength += QPackWire.WriteInt(_encoderStream, 0b0010_0000, 5, _capacity); // Set Dynamic Table Capacity
            _capacitySent = true;
        }

        // Base = entries acknowledged when the section starts, so every reference is pre-base and its
        // relative index is known while writing (RFC 9204 4.5.1.2); Delta Base = Base - Required Insert Count.
        var baseIndex = _knownReceivedCount;
        var references = _listPool.Count > 0 ? _listPool.Pop() : new List<Entry>();
        var requiredInsertCount = 0;
        var lineLength = 0;
        foreach (var (name, value) in headers)
        {
            var destination = _lines.AsSpan(lineLength);
            if (!_indexSensitive && IsSensitive(name, value))
            {
                lineLength += WriteNeverIndexed(name, value, destination);
                continue;
            }

            var state = GetState(name);
            var (exact, pending, newestAcknowledged) = Scan(state, value);
            if (exact is not null)
            {
                Reference(exact, references, ref requiredInsertCount);
                lineLength += QPackWire.WriteInt(destination, 0b1000_0000, 6, baseIndex - 1 - exact.AbsoluteIndex); // 1T, T=0
                continue;
            }

            if (_static.TryGetExact(name, value, out var staticIndex))
            {
                lineLength += QPackWire.WriteInt(destination, 0b1100_0000, 6, staticIndex); // 1T, T=1
                continue;
            }

            int prefix;
            if (_static.TryGetName(name, out var staticName))
            {
                prefix = QPackWire.WriteInt(destination, 0b0101_0000, 4, staticName); // 01NT, T=1
            }
            else if (newestAcknowledged is not null)
            {
                Reference(newestAcknowledged, references, ref requiredInsertCount);
                prefix = QPackWire.WriteInt(destination, 0b0100_0000, 4, baseIndex - 1 - newestAcknowledged.AbsoluteIndex); // 01NT, T=0
            }
            else
            {
                prefix = QPackWire.WriteString(destination, 0b0010_0000, 3, name); // 001NH, literal name
            }

            lineLength += prefix + QPackWire.WriteString(destination[prefix..], 0, 7, value);
            if (!pending)
            {
                encoderLength += MaybeInsert(state, value, encoderLength);
            }
        }

        var blockLength = QPackWire.WriteInt(_block, 0, 8, EncodeRequiredInsertCount(requiredInsertCount));
        var deltaBase = requiredInsertCount == 0 ? 0 : baseIndex - requiredInsertCount;
        blockLength += QPackWire.WriteInt(_block.AsSpan(blockLength), 0, 7, deltaBase); // S = 0
        _lines.AsSpan(0, lineLength).CopyTo(_block.AsSpan(blockLength));
        blockLength += lineLength;

        _pending.Enqueue(new PendingSection(_section++, _insertCount, references));
        return (encoderLength, blockLength);
    }

    // Exact acknowledged match, whether the value is already inserted but unacknowledged, and the newest
    // acknowledged entry for the name (for a name reference). Newest first; values compared, not hashed.
    private (Entry? Exact, bool Pending, Entry? NewestAcknowledged) Scan(NameState state, string value)
    {
        Entry? newestAcknowledged = null;
        for (var i = state.Entries.Count - 1; i >= 0; i--)
        {
            var entry = state.Entries[i];
            var acknowledged = entry.AbsoluteIndex < _knownReceivedCount;
            if (string.Equals(entry.Value, value, StringComparison.Ordinal))
            {
                return acknowledged ? (entry, false, entry) : (null, true, newestAcknowledged);
            }

            newestAcknowledged ??= acknowledged ? entry : null;
        }

        return (null, false, newestAcknowledged);
    }

    private int MaybeInsert(NameState state, string value, int encoderOffset)
    {
        if (Array.IndexOf(state.Recent, value) < 0)
        {
            state.Recent[state.RecentNext] = value;
            state.RecentNext = (state.RecentNext + 1) % RecentValues;
            return 0;
        }

        var entry = new Entry(state, value, _insertCount);
        if (entry.Size > _capacity / 2 || !MakeRoom(entry.Size))
        {
            return 0;
        }

        var destination = _encoderStream.AsSpan(encoderOffset);
        int written;
        if (_static.TryGetName(state.Name, out var staticName))
        {
            written = QPackWire.WriteInt(destination, 0b1100_0000, 6, staticName); // Insert with Name Reference, T=1
        }
        else if (state.Entries.Count > 0)
        {
            var nameEntry = state.Entries[^1];
            written = QPackWire.WriteInt(destination, 0b1000_0000, 6, _insertCount - 1 - nameEntry.AbsoluteIndex); // T=0
        }
        else
        {
            written = QPackWire.WriteString(destination, 0b0100_0000, 5, state.Name); // Insert with Literal Name
        }

        written += QPackWire.WriteString(destination[written..], 0, 7, value);

        _table.Enqueue(entry);
        state.Entries.Add(entry);
        _tableSize += entry.Size;
        _insertCount++;
        return written;
    }

    private bool MakeRoom(int size)
    {
        while (_tableSize + size > _capacity)
        {
            var oldest = _table.Peek();
            if (oldest.AbsoluteIndex >= _knownReceivedCount || oldest.UnacknowledgedReferences > 0)
            {
                return false; // not evictable yet (RFC 9204 2.1.1)
            }

            _table.Dequeue();
            _tableSize -= oldest.Size;
            oldest.State.Entries.Remove(oldest); // the oldest entry for its name, so normally index 0
        }

        return true;
    }

    private void ApplyAcknowledgements()
    {
        // Everything the peer did for sections <= _section - ackDelay - 1 has reached us by now.
        while (_pending.Count > 0 && _pending.Peek().Section <= _section - _ackDelay - 1)
        {
            var done = _pending.Dequeue();
            _knownReceivedCount = Math.Max(_knownReceivedCount, done.InsertCountAfter); // Insert Count Increment
            foreach (var entry in done.References)
            {
                entry.UnacknowledgedReferences--; // Section Acknowledgment
            }

            done.References.Clear();
            _listPool.Push(done.References);
        }
    }

    private static bool IsSensitive(string name, string value)
        => name == "authorization" || (name == "cookie" && value.Length < 20);

    // Literal with the N (never-index) bit set, so intermediaries must not index it either.
    private int WriteNeverIndexed(string name, string value, Span<byte> destination)
    {
        var prefix = _static.TryGetName(name, out var staticName)
            ? QPackWire.WriteInt(destination, 0b0111_0000, 4, staticName) // 01NT, N=1, T=1
            : QPackWire.WriteString(destination, 0b0011_0000, 3, name); // 001NH, N=1
        return prefix + QPackWire.WriteString(destination[prefix..], 0, 7, value);
    }

    private NameState GetState(string name)
    {
        if (!_names.TryGetValue(name, out var state))
        {
            _names[name] = state = new NameState(name);
        }

        return state;
    }

    private static void Reference(Entry entry, List<Entry> references, ref int requiredInsertCount)
    {
        entry.UnacknowledgedReferences++;
        references.Add(entry);
        requiredInsertCount = Math.Max(requiredInsertCount, entry.AbsoluteIndex + 1);
    }

    private int EncodeRequiredInsertCount(int requiredInsertCount)
        => requiredInsertCount == 0 ? 0 : requiredInsertCount % (2 * _maxEntries) + 1;
}
