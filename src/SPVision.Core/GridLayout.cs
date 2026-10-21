namespace SPVision.Core;

/// <summary>Position of one tile in a <see cref="GridLayout"/>, in zero-based grid cells.</summary>
public readonly record struct GridCell(int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

/// <summary>
/// Live view layouts, identified by their tile count (which is also what settings store).
/// 6 and 8 put a large tile top-left with the small tiles down the right column and along
/// the bottom row; 25 and 32 are uniform grids.
/// </summary>
public sealed class GridLayout
{
    public static readonly GridLayout Six = new(columns: 3, rows: 3, bigSpan: 2);
    public static readonly GridLayout Eight = new(columns: 4, rows: 4, bigSpan: 3);
    public static readonly GridLayout TwentyFive = new(columns: 5, rows: 5);
    public static readonly GridLayout ThirtyTwo = new(columns: 8, rows: 4);

    /// <summary>Smallest first.</summary>
    public static IReadOnlyList<GridLayout> All { get; } = [Six, Eight, TwentyFive, ThirtyTwo];

    /// <summary>Fresh installs: all 22 cameras of the recorder fit.</summary>
    public static GridLayout Default => TwentyFive;

    public static int MaxTiles { get; } = All.Max(l => l.TileCount);

    readonly GridCell[] _cells;

    GridLayout(int columns, int rows, int bigSpan = 1)
    {
        Columns = columns;
        Rows = rows;
        // Tile 1 takes the top-left block; the rest follow in reading order (right column, then bottom row).
        var cells = new List<GridCell> { new(0, 0, bigSpan, bigSpan) };
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                if (row >= bigSpan || column >= bigSpan)
                {
                    cells.Add(new GridCell(row, column));
                }
            }
        }
        _cells = [.. cells];
    }

    public int Columns { get; }
    public int Rows { get; }
    public int TileCount => _cells.Length;

    /// <summary>Cell of each tile, by tile index.</summary>
    public IReadOnlyList<GridCell> Cells => _cells;

    /// <summary>Smallest layout with at least <paramref name="tileCount"/> tiles; the largest one if none is big enough.</summary>
    public static GridLayout Fitting(int tileCount) =>
        All.FirstOrDefault(l => l.TileCount >= tileCount) ?? All[^1];

    /// <summary>
    /// Layout of a saved view. Versions up to the 1/4/9/16 grids stored only the square grid's column
    /// count; those (and any unknown tile count) map to the smallest layout that holds as many tiles.
    /// </summary>
    public static GridLayout ForSavedView(SavedView? view) => view switch
    {
        { Layout: int tileCount and > 0 } => Fitting(tileCount),
        { Columns: int columns and >= 1 and <= 4 } => Fitting(columns * columns),
        _ => Default,
    };

    public override string ToString() => TileCount.ToString();
}
