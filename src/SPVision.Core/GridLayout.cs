namespace SPVision.Core;

/// <summary>Position of one tile in a <see cref="GridLayout"/>, in zero-based grid cells.</summary>
public readonly record struct GridCell(int Row, int Column, int RowSpan = 1, int ColumnSpan = 1);

/// <summary>
/// Tile layouts, identified by their tile count (which is also what settings store). Every layout is a
/// square grid, so all tiles keep the same aspect ratio. "Standart" layouts are uniform grids; the
/// "büyük kareli" ones put one or more square big tiles into the grid. Tile order: big tiles first
/// (in the order they are defined), then the remaining cells in reading order.
/// </summary>
public sealed class GridLayout
{
    // ---- Standart: uniform square grids ----
    public static readonly GridLayout One = new(1);
    public static readonly GridLayout Four = new(2);
    public static readonly GridLayout Nine = new(3);
    public static readonly GridLayout Sixteen = new(4);
    public static readonly GridLayout TwentyFive = new(5);
    public static readonly GridLayout ThirtySix = new(6);

    // ---- Büyük kareli: big tiles given as (column, row, span), 1-based like the approved mockup ----
    public static readonly GridLayout Six = new(3, (1, 1, 2));
    public static readonly GridLayout Seven = new(4, (1, 1, 2), (3, 1, 2), (1, 3, 2));
    public static readonly GridLayout Eight = new(4, (1, 1, 3));
    public static readonly GridLayout Ten = new(4, (1, 1, 2), (3, 1, 2));
    public static readonly GridLayout Thirteen = new(4, (2, 2, 2));
    public static readonly GridLayout TwentyTwo = new(5, (1, 1, 2));

    /// <summary>Layout picker, first section ("STANDART").</summary>
    public static IReadOnlyList<GridLayout> Standard { get; } = [One, Four, Nine, Sixteen, TwentyFive, ThirtySix];

    /// <summary>Layout picker, second section ("BÜYÜK KARELİ").</summary>
    public static IReadOnlyList<GridLayout> BigTile { get; } = [Six, Seven, Eight, Ten, Thirteen, TwentyTwo];

    /// <summary>Every live layout, smallest first.</summary>
    public static IReadOnlyList<GridLayout> All { get; } = [.. Standard.Concat(BigTile).OrderBy(l => l.TileCount)];

    /// <summary>Playback (synchronous multi-camera) offers at most 16 tiles.</summary>
    public const int PlaybackMaxTiles = 16;

    public static IReadOnlyList<GridLayout> PlaybackStandard { get; } = [.. Standard.Where(l => l.TileCount <= PlaybackMaxTiles)];

    public static IReadOnlyList<GridLayout> PlaybackBigTile { get; } = [.. BigTile.Where(l => l.TileCount <= PlaybackMaxTiles)];

    /// <summary>Fresh installs: all 22 cameras of the recorder fit.</summary>
    public static GridLayout Default => TwentyFive;

    /// <summary>Playback starts with four cameras side by side.</summary>
    public static GridLayout PlaybackDefault => Four;

    public static int MaxTiles { get; } = All.Max(l => l.TileCount);

    /// <summary>Column counts of the 1/4/9/16 era (settings stored only the square grid's column count).</summary>
    static readonly Dictionary<int, int> LegacyColumns = new() { [1] = 6, [2] = 6, [3] = 25, [4] = 25 };

    /// <summary>Tile counts that no longer exist: the old 8×4 grid made tall, squeezed tiles.</summary>
    static readonly Dictionary<int, int> RetiredLayouts = new() { [32] = 36 };

    readonly GridCell[] _cells;

    GridLayout(int size, params (int Column, int Row, int Span)[] bigTiles)
    {
        Columns = size;
        Rows = size;
        var used = new bool[size, size];
        var cells = new List<GridCell>();
        foreach (var (column, row, span) in bigTiles)
        {
            for (var r = row - 1; r < row - 1 + span; r++)
            {
                for (var c = column - 1; c < column - 1 + span; c++)
                {
                    used[r, c] = true;
                }
            }
            cells.Add(new GridCell(row - 1, column - 1, span, span));
        }
        for (var row = 0; row < size; row++)
        {
            for (var column = 0; column < size; column++)
            {
                if (!used[row, column])
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

    /// <summary>True for the uniform ("standart") grids.</summary>
    public bool IsUniform => _cells.All(c => c.RowSpan == 1 && c.ColumnSpan == 1);

    /// <summary>Cell of each tile, by tile index.</summary>
    public IReadOnlyList<GridCell> Cells => _cells;

    /// <summary>Smallest layout with at least <paramref name="tileCount"/> tiles; the largest one if none is big enough.</summary>
    public static GridLayout Fitting(int tileCount) =>
        All.FirstOrDefault(l => l.TileCount >= tileCount) ?? All[^1];

    /// <summary>The layout with exactly this tile count, if there is one.</summary>
    public static GridLayout? WithTileCount(int tileCount) => All.FirstOrDefault(l => l.TileCount == tileCount);

    /// <summary>
    /// Layout of a saved view. A saved 32 (8×4) becomes 36 (6×6); other unknown tile counts map to the
    /// smallest layout that holds as many tiles. Versions up to the 1/4/9/16 grids stored only the
    /// column count: 1×1 and 2×2 open as 6, 3×3 and 4×4 as 25 (as before). Tile assignments are kept
    /// in order by the caller; the first <see cref="TileCount"/> of them are shown.
    /// </summary>
    public static GridLayout ForSavedView(SavedView? view) => view switch
    {
        { Layout: int tileCount and > 0 } when RetiredLayouts.TryGetValue(tileCount, out var replacement) => WithTileCount(replacement)!,
        { Layout: int tileCount and > 0 } => WithTileCount(tileCount) ?? Fitting(tileCount),
        { Columns: int columns } when LegacyColumns.TryGetValue(columns, out var tiles) => WithTileCount(tiles)!,
        _ => Default,
    };

    public override string ToString() => TileCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
