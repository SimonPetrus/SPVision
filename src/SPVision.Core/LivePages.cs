namespace SPVision.Core;

/// <summary>
/// Paging of the live wall. One global, ordered slot list holds the cameras; a layout with N tiles
/// shows page p = slots [p*N, (p+1)*N). Dropping a camera onto a tile or swapping two tiles changes
/// positions in that global order, so an arrangement survives page and layout changes. Every listed
/// camera is in the list once (<see cref="Reconcile"/>); an empty slot ("hole") is a tile the user
/// closed or an empty tile a camera was moved away from. Trailing holes are dropped, so the pages
/// end with the last camera; empty tiles of the last page show the empty state.
/// </summary>
public sealed class LivePages
{
    readonly List<CameraRef?> _slots = [];

    public LivePages(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        PageSize = pageSize;
    }

    /// <summary>The global order; may contain holes, never ends with one.</summary>
    public IReadOnlyList<CameraRef?> Slots => _slots;

    /// <summary>Tile count of the current layout.</summary>
    public int PageSize { get; private set; }

    /// <summary>Zero-based current page, always within <see cref="PageCount"/>.</summary>
    public int Page { get; private set; }

    public int PageCount => CountPages(_slots.Count, PageSize);

    /// <summary>Camera of each tile on the current page (exactly <see cref="PageSize"/> entries, null = empty tile).</summary>
    public IReadOnlyList<CameraRef?> CurrentPage => Slice(_slots, Page, PageSize);

    /// <summary>Position in the global order of a tile of the current page.</summary>
    public int SlotOf(int tile) => Page * PageSize + tile;

    /// <summary>
    /// A camera group is shown (<see cref="CameraGroups"/>): only its cameras, each in its saved tile. Empty tiles stay
    /// empty and cameras that are not in the group are not added; a camera that disappears leaves a hole (nothing moves up).
    /// </summary>
    public bool IsGroup { get; private set; }

    /// <summary>Global position of the camera, or -1 when it is in no slot.</summary>
    public int IndexOf(CameraRef camera) => _slots.IndexOf(camera);

    // ---- Pure helpers ----

    /// <summary>Pages needed for <paramref name="slotCount"/> slots; at least one (an empty wall is one empty page).</summary>
    public static int CountPages(int slotCount, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        return Math.Max(1, (Math.Max(0, slotCount) + pageSize - 1) / pageSize);
    }

    public static int Clamp(int page, int pageCount) => Math.Clamp(page, 0, Math.Max(1, pageCount) - 1);

    /// <summary>The page <paramref name="delta"/> steps away, wrapping around (last → first, first → last).</summary>
    public static int Wrap(int page, int delta, int pageCount)
    {
        var count = Math.Max(1, pageCount);
        return ((page + delta) % count + count) % count;
    }

    /// <summary>Page that shows the slot at <paramref name="slot"/>.</summary>
    public static int PageContaining(int slot, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        return Math.Max(0, slot) / pageSize;
    }

    /// <summary>Slots [page*pageSize, (page+1)*pageSize), padded with empty tiles.</summary>
    public static CameraRef?[] Slice(IReadOnlyList<CameraRef?> slots, int page, int pageSize)
    {
        var tiles = new CameraRef?[pageSize];
        var start = page * pageSize;
        for (var i = 0; i < pageSize && start + i < slots.Count; i++)
        {
            tiles[i] = slots[start + i];
        }
        return tiles;
    }

    /// <summary>
    /// Which existing tile shows each position after a change, so a tile that already shows a wanted
    /// camera only moves (no reconnect). <paramref name="current"/> = camera of every tile now (all of them,
    /// in position order), <paramref name="wanted"/> = camera of each position of the new page. Returns
    /// order[i] = index in <paramref name="current"/> of the tile for position i (a permutation of all
    /// tiles). Positions whose camera is not shown anywhere keep their own tile when it is free.
    /// </summary>
    public static int[] AssignTiles(IReadOnlyList<CameraRef?> current, IReadOnlyList<CameraRef?> wanted)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(wanted.Count, current.Count);
        var order = Enumerable.Repeat(-1, current.Count).ToArray();
        var used = new bool[current.Count];
        for (var i = 0; i < wanted.Count; i++)
        {
            if (wanted[i] is not { } camera)
            {
                continue;
            }
            var tile = !used[i] && current[i] == camera ? i : -1;
            for (var j = 0; tile < 0 && j < current.Count; j++)
            {
                if (!used[j] && current[j] == camera)
                {
                    tile = j;
                }
            }
            if (tile >= 0)
            {
                order[i] = tile;
                used[tile] = true;
            }
        }
        for (var i = 0; i < order.Length; i++)
        {
            if (order[i] >= 0)
            {
                continue;
            }
            var tile = !used[i] ? i : Array.IndexOf(used, false);
            order[i] = tile;
            used[tile] = true;
        }
        return order;
    }

    // ---- State changes ----

    /// <summary>
    /// Startup: the saved order (or none), brought up to date with the cameras listed now, and the saved
    /// page, clamped to what exists. <paramref name="group"/>: the slots are a camera group's (<see cref="IsGroup"/>).
    /// </summary>
    public void Load(IEnumerable<CameraRef?> saved, IReadOnlyList<CameraRef> listed, int page, int pageSize, bool group = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        _slots.Clear();
        _slots.AddRange(saved);
        PageSize = pageSize;
        Page = page;
        IsGroup = group;
        Reconcile(listed);
    }

    /// <summary>
    /// "Show all cameras of a device": these cameras come first in this order, the other listed ones
    /// follow in list order; back to the first page. Leaves a shown group.
    /// </summary>
    public void Reset(IEnumerable<CameraRef> first, IReadOnlyList<CameraRef> listed)
    {
        _slots.Clear();
        _slots.AddRange(first.Distinct());
        Page = 0;
        IsGroup = false;
        Reconcile(listed);
    }

    /// <summary>
    /// The camera list changed (or was loaded): cameras that are no longer listed (removed, hidden) leave
    /// the order and the cameras behind them move up; duplicates are dropped; listed cameras that are
    /// missing fill the holes first (in list order), the rest go to the end. A group (<see cref="IsGroup"/>)
    /// keeps its tiles: unlisted and repeated cameras become holes, nothing moves and nothing is added.
    /// </summary>
    public void Reconcile(IReadOnlyList<CameraRef> listed)
    {
        var allowed = listed.ToHashSet();
        var seen = new HashSet<CameraRef>();
        if (IsGroup)
        {
            for (var i = 0; i < _slots.Count; i++)
            {
                if (_slots[i] is { } camera && (!allowed.Contains(camera) || !seen.Add(camera)))
                {
                    _slots[i] = null;
                }
            }
            Normalize();
            return;
        }
        for (var i = _slots.Count - 1; i >= 0; i--)
        {
            if (_slots[i] is { } camera && !allowed.Contains(camera))
            {
                _slots.RemoveAt(i);
            }
        }
        for (var i = 0; i < _slots.Count; i++)
        {
            if (_slots[i] is { } camera && !seen.Add(camera))
            {
                _slots[i] = null;
            }
        }
        var hole = 0;
        foreach (var camera in listed.Where(c => !seen.Contains(c)).Distinct())
        {
            while (hole < _slots.Count && _slots[hole] is not null)
            {
                hole++;
            }
            if (hole < _slots.Count)
            {
                _slots[hole] = camera;
            }
            else
            {
                _slots.Add(camera);
            }
        }
        Normalize();
    }

    /// <summary>Goes to a page (clamped). Returns true when the page changed.</summary>
    public bool GoTo(int page)
    {
        var target = Clamp(page, PageCount);
        if (target == Page)
        {
            return false;
        }
        Page = target;
        return true;
    }

    /// <summary>Previous/next page with wrap-around. Returns true when the page changed.</summary>
    public bool Step(int delta) => GoTo(Wrap(Page, delta, PageCount));

    /// <summary>Layout change: the first camera of the current page stays visible, on the page that now contains it.</summary>
    public void SetPageSize(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var start = Page * PageSize;
        var anchor = start;
        for (var i = start; i < Math.Min(start + PageSize, _slots.Count); i++)
        {
            if (_slots[i] is not null)
            {
                anchor = i;
                break;
            }
        }
        PageSize = pageSize;
        Page = Clamp(PageContaining(anchor, pageSize), PageCount);
    }

    /// <summary>
    /// A camera was dropped onto a tile of the current page. If it already has a slot the two trade places
    /// (the tile's camera goes where the dropped one was); otherwise the tile's camera moves to the first
    /// hole, or to the end, so no camera drops out of the pages.
    /// </summary>
    public void Place(int tile, CameraRef camera)
    {
        var target = SlotOf(tile);
        Grow(target);
        var existing = _slots.IndexOf(camera);
        if (existing >= 0)
        {
            (_slots[existing], _slots[target]) = (_slots[target], _slots[existing]);
        }
        else
        {
            var displaced = _slots[target];
            _slots[target] = camera;
            if (displaced is not null)
            {
                var hole = _slots.IndexOf(null);
                if (hole >= 0)
                {
                    _slots[hole] = displaced;
                }
                else
                {
                    _slots.Add(displaced);
                }
            }
        }
        Normalize();
    }

    /// <summary>A tile was dragged onto another tile of the current page: their slots trade places (an empty one too).</summary>
    public void Swap(int tileA, int tileB)
    {
        var a = SlotOf(tileA);
        var b = SlotOf(tileB);
        Grow(Math.Max(a, b));
        (_slots[a], _slots[b]) = (_slots[b], _slots[a]);
        Normalize();
    }

    /// <summary>The user closed a tile: its slot becomes a hole.</summary>
    public void Clear(int tile)
    {
        var slot = SlotOf(tile);
        if (slot < _slots.Count)
        {
            _slots[slot] = null;
            Normalize();
        }
    }

    void Grow(int index)
    {
        while (_slots.Count <= index)
        {
            _slots.Add(null);
        }
    }

    /// <summary>Drops trailing holes and keeps the page within range.</summary>
    void Normalize()
    {
        while (_slots.Count > 0 && _slots[^1] is null)
        {
            _slots.RemoveAt(_slots.Count - 1);
        }
        Page = Clamp(Page, PageCount);
    }
}
