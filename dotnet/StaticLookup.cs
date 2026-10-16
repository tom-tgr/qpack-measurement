using System.Net.Http.QPack;
using System.Text;

// QPACK static table lookups, shared by both encoders so their CPU cost is identical in the benchmarks.
// Keyed by name only: hashing a 1 KB value (e.g. a bearer token) on every request costs far more than
// comparing it against the handful of static values for that name.
public sealed class StaticLookup
{
    public static StaticLookup Instance { get; } = new();

    private readonly Dictionary<string, (string Value, int Index)[]> _byName = new();

    private StaticLookup()
    {
        var byName = new Dictionary<string, List<(string, int)>>();
        for (var i = 0; i < H3StaticTable.Count; i++)
        {
            var field = H3StaticTable.Get(i);
            var name = Encoding.ASCII.GetString(field.Name);
            if (!byName.TryGetValue(name, out var values))
            {
                byName[name] = values = new List<(string, int)>();
            }

            values.Add((Encoding.ASCII.GetString(field.Value), i));
        }

        foreach (var (name, values) in byName)
        {
            _byName[name] = values.ToArray(); // in table order, so [0] is the first index for the name
        }
    }

    public bool TryGetExact(string name, string value, out int index)
    {
        if (_byName.TryGetValue(name, out var values))
        {
            foreach (var candidate in values)
            {
                if (string.Equals(candidate.Value, value, StringComparison.Ordinal))
                {
                    index = candidate.Index;
                    return true;
                }
            }
        }

        index = -1;
        return false;
    }

    public bool TryGetName(string name, out int index)
    {
        var found = _byName.TryGetValue(name, out var values);
        index = found ? values![0].Index : -1;
        return found;
    }
}
