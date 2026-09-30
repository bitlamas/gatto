namespace Gatto.Terminal;

//the areas the keys can be in, in tab order. files and builds are separate stops so nobody must scroll thirty quants to reach builds
public enum Region
{
    Strip,
    Families,
    //the publisher slot, a control now, Enter opens the picker. ordered after Families to follow the chips row left to right
    Publisher,
    List,
    Files,
    Builds,
    //the region that takes typed input, shown as search on the shelf. the same component wherever it appears
    Search,
}

//the thick arrow marks where the keys are, the thin one what is remembered. esc returns to the list from any other region, in the list it arms the leave warning
public sealed class FocusRing
{
    private readonly IReadOnlyList<Region> _order;
    private int _at;

    //the screen's regions in Tab order, each screen declares its own. keys start at the list when the screen has one
    public FocusRing(IReadOnlyList<Region> order, Region? start = null)
    {
        if (order.Count == 0) throw new ArgumentException("a screen has at least one region", nameof(order));
        _order = order;
        var wanted = start ?? (order.Contains(Region.List) ? Region.List : order[0]);
        _at = Math.Max(0, order.ToList().IndexOf(wanted));
    }

    public Region Current => _order[_at];

    //a copy fixed at the current position. the factory composes on the painter's thread, input must not move under it
    public FocusRing Frozen() => new(_order, Current);

    //tab, wraps at the ends. a ring with an end makes the user remember which way they came
    public Region Next()
    {
        _at = (_at + 1) % _order.Count;
        return Current;
    }

    //esc's first job, getting back to the list. true when the press was spent, only a press already in the list may arm leaving
    public bool EscReturnsToList()
    {
        if (Current == Region.List || !_order.Contains(Region.List)) return false;
        _at = _order.ToList().IndexOf(Region.List);
        return true;
    }

    //a click focuses a region directly, so it isn't Tab in a loop
    public bool Focus(Region r)
    {
        var i = _order.ToList().IndexOf(r);
        if (i < 0) return false;
        _at = i;
        return true;
    }

    //whether a region is in this ring. the footer lists only keys that have a deed
    public bool Has(Region r) => _order.Contains(r);

    //the footer's Esc verb, read from where the keys are so it can't disagree. atList is what the caller's handler really does on the list
    public string EscVerb(string atList) =>
        Current == Region.List || !_order.Contains(Region.List) ? atList : "back";

    //whether Tab has anywhere to go, read from the list Tab itself uses, so the footer can't advertise a dead key
    public bool HasSecondArea => _order.Count > 1;
}
