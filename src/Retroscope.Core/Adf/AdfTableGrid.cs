namespace Retroscope.Core.Adf;

/// <summary>The table_shape rule: a table's cells, spans counted, cover a rectangle of at most 64 columns.</summary>
internal static class AdfTableGrid {
    /// <returns>Null when a cell's colwidth does not give one width per spanned column; else whether the grid is
    /// rectangular, with at least one row, no row spanning past the last row and no cell overlapping another.</returns>
    public static bool? Check(IEnumerable<IEnumerable<(int Colspan, int Rowspan, int Widths)>> rows) {
        var cells = rows.Select(row => row.ToList()).ToList();
        if (cells.Count == 0) {
            return false;
        }
        var occupied = cells.Select(_ => new HashSet<int>()).ToList();
        var regular = true;
        for (var r = 0; r < cells.Count; r++) {
            var column = 0;
            foreach (var (colspan, rowspan, widths) in cells[r]) {
                if (widths != colspan) {
                    return null;
                }
                while (occupied[r].Contains(column)) {
                    column++;
                }
                regular &= r + rowspan <= cells.Count;
                for (var dr = 0; dr < rowspan && r + dr < cells.Count; dr++) {
                    for (var dc = 0; dc < colspan; dc++) {
                        regular &= occupied[r + dr].Add(column + dc);
                    }
                }
                column += colspan;
            }
        }
        var width = occupied[0].Count;
        return regular && width is > 0 and <= AdfSchema.MaxColumns && occupied.All(row => row.Count == width && row.Max() == width - 1);
    }
}
