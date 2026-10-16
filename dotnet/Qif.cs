// Shared by the baseline tool and the benchmarks.

public static class Qif
{
    // QIF: "name<TAB>value" lines, blank line between header lists, '#' comments.
    public static IEnumerable<List<(string Name, string Value)>> Read(string path)
    {
        var current = new List<(string, string)>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            if (line.Length == 0)
            {
                if (current.Count > 0)
                {
                    yield return current;
                    current = new List<(string, string)>();
                }

                continue;
            }

            var tab = line.IndexOf('\t');
            current.Add(tab < 0 ? (line, "") : (line[..tab], line[(tab + 1)..]));
        }

        if (current.Count > 0)
        {
            yield return current;
        }
    }
}
