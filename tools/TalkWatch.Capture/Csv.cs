using System.Text;

namespace TalkWatch.Capture;

/// <summary>Just enough RFC 4180 to split and rejoin one line: quoted fields, doubled quotes, commas inside quotes.</summary>
internal static class Csv
{
    public static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }

    public static string Join(IEnumerable<string> cells) =>
        string.Join(',', cells.Select(c => c.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{c.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : c));
}
