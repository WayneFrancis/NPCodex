using System.Data;

namespace NPCodex.Data;

/// <summary>Literal, case-insensitive column filters for WinForms DataViews.
/// Handles punctuation as data, not RowFilter expressions.</summary>
public static class GridTextFilter
{
    public static string Build(
        DataColumnCollection columns, IEnumerable<KeyValuePair<int,string>> filters)
    {
        var clauses=new List<string>();
        foreach(var (index, raw) in filters)
        {
            if(index<0 || index>=columns.Count || string.IsNullOrWhiteSpace(raw))
                continue;
            var name=columns[index].ColumnName.Replace("]", "]]");
            var literal=raw.Trim().Replace("'", "''")
                .Replace("[", "[[]")
                .Replace("*", "[*]")
                .Replace("%", "[%]");
            clauses.Add($"CONVERT([{name}], 'System.String') LIKE '%{literal}%'");
        }
        return string.Join(" AND ",clauses);
    }
}
