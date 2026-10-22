using System.Globalization;

namespace SPVision.Core;

/// <summary>
/// A camera group ("Kamera grupları", live sidebar): a named grid layout with the camera of every tile, saved from the
/// screen ("Grup kaydet") and brought back exactly by clicking it. Kept in <see cref="AppSettings.Groups"/>. Cameras are
/// referenced by device id + ISAPI channel, so a removed device or camera only leaves its tile empty.
/// </summary>
public sealed class CameraGroup
{
    /// <summary>Stable id: <see cref="AppSettings.ActiveGroup"/> refers to it, so a renamed group stays active.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>Tile count of the <see cref="GridLayout"/>, like <see cref="SavedView.Layout"/> (a retired 32 opens as 36).</summary>
    public int Layout { get; set; }

    /// <summary>Camera of each tile in tile order (as many as the layout has tiles when saved); null = empty tile.</summary>
    public List<CameraRef?> Tiles { get; set; } = [];
}

/// <summary>
/// Rules of the camera groups: name validation, capturing the screen, and the tiles a group shows with the cameras that
/// exist now. The built-in first row "Tüm kameralar" is not a stored group: it is the normal arrangement (every listed
/// camera in list order, default layout) and the state when no group is active.
/// </summary>
public static class CameraGroups
{
    public const string AllCamerasName = "Tüm kameralar";

    /// <summary>Prefilled name of a new group ("Yeni grup", then "Yeni grup 2"… when taken).</summary>
    public const string DefaultName = "Yeni grup";

    public const int MaxNameLength = 40;

    static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static CameraGroup? Find(IEnumerable<CameraGroup> groups, Guid? id) =>
        id is { } value ? groups.FirstOrDefault(g => g.Id == value) : null;

    /// <summary>
    /// Error text for a group name, or null when it can be used: not empty, at most <see cref="MaxNameLength"/> characters,
    /// no control characters, and not the name of another group (<paramref name="except"/> = the group being renamed) or of
    /// "Tüm kameralar", ignoring case (Turkish and invariant: "GİRİŞ", "GIRIŞ" and "giriş" are the same name).
    /// </summary>
    public static string? CheckName(string? name, IEnumerable<CameraGroup> groups, Guid? except = null)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return "Grup adı boş olamaz.";
        }
        if (trimmed.Length > MaxNameLength)
        {
            return $"Grup adı en çok {MaxNameLength} karakter olabilir.";
        }
        if (trimmed.Any(char.IsControl))
        {
            return "Grup adında geçersiz karakter var.";
        }
        if (SameName(trimmed, AllCamerasName) || groups.Any(g => g.Id != except && SameName(g.Name ?? "", trimmed)))
        {
            return "Bu adda bir grup zaten var.";
        }
        return null;
    }

    /// <summary>Same group name: case-insensitive in Turkish (İ/i, I/ı) or invariant (I/i), surrounding spaces ignored.</summary>
    public static bool SameName(string a, string b)
    {
        a = a.Trim();
        b = b.Trim();
        return string.Compare(a, b, Turkish, CompareOptions.IgnoreCase) == 0 ||
               string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>"Yeni grup", or "Yeni grup 2", "Yeni grup 3"… — the first one no group uses.</summary>
    public static string UniqueName(IEnumerable<CameraGroup> groups)
    {
        var list = groups.ToList();
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? DefaultName : $"{DefaultName} {n}";
            if (CheckName(name, list) is null)
            {
                return name;
            }
        }
    }

    /// <summary>"Grup kaydet": the layout and the camera of every tile on screen (the current page).</summary>
    public static CameraGroup Capture(string name, GridLayout layout, IReadOnlyList<CameraRef?> screen) => new()
    {
        Name = name.Trim(),
        Layout = layout.TileCount,
        Tiles = TilesOf(layout, screen),
    };

    /// <summary>"Ekrandaki düzenle güncelle": the group takes the layout and cameras on screen; name and id stay.</summary>
    public static void Overwrite(CameraGroup group, GridLayout layout, IReadOnlyList<CameraRef?> screen)
    {
        group.Layout = layout.TileCount;
        group.Tiles = TilesOf(layout, screen);
    }

    /// <summary>Layout of the group; a retired or unknown tile count maps like a saved view (<see cref="GridLayout.ForSavedView"/>).</summary>
    public static GridLayout LayoutOf(CameraGroup group) => GridLayout.ForSavedView(new SavedView { Layout = group.Layout });

    /// <summary>
    /// The group's tiles as they show now: one entry per tile of its layout, each camera in its saved tile. A camera that
    /// is not listed any more (device or camera removed, "Listede göster" off) and a repeated camera leave their tile
    /// empty; the others do not move.
    /// </summary>
    public static CameraRef?[] Slots(CameraGroup group, IEnumerable<CameraRef> listed)
    {
        var slots = new CameraRef?[LayoutOf(group).TileCount];
        var allowed = listed.ToHashSet();
        var seen = new HashSet<CameraRef>();
        var tiles = group.Tiles ?? [];
        for (var i = 0; i < slots.Length && i < tiles.Count; i++)
        {
            if (tiles[i] is { } camera && allowed.Contains(camera) && seen.Add(camera))
            {
                slots[i] = camera;
            }
        }
        return slots;
    }

    /// <summary>Cameras the group shows now (the count on its sidebar row).</summary>
    public static int CameraCount(CameraGroup group, IEnumerable<CameraRef> listed) => Slots(group, listed).Count(s => s is not null);

    /// <summary>Preview text of the "Grup kaydet" dialog.</summary>
    public static string SaveSummary(GridLayout layout, int cameras) =>
        $"Ekrandaki {layout.TileCount} kareli ızgara ve {cameras} kameranın yeri kaydedilir. Grubu seçince aynen geri gelir.";

    /// <summary>Status bar: "Grup: &lt;ad&gt;" ("Tüm kameralar" when no group is active).</summary>
    public static string StatusText(CameraGroup? active) => "Grup: " + (active?.Name ?? AllCamerasName);

    /// <summary>One entry per tile of the layout (missing ones empty, extra ones dropped); a repeated camera keeps its first tile.</summary>
    static List<CameraRef?> TilesOf(GridLayout layout, IReadOnlyList<CameraRef?> screen)
    {
        var seen = new HashSet<CameraRef>();
        var tiles = new List<CameraRef?>(layout.TileCount);
        for (var i = 0; i < layout.TileCount; i++)
        {
            tiles.Add(i < screen.Count && screen[i] is { } camera && seen.Add(camera) ? camera : null);
        }
        return tiles;
    }
}
