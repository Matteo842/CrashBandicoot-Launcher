namespace RecompOne.Runtime.Host.Window;

public static class DevMenuRegistry
{
    static readonly List<IDevMenuSection> _sections = [];
    static bool _dirty;

    public static void Register(IDevMenuSection section)
    {
        if (section == null) return;
        _sections.RemoveAll(s => s.Id == section.Id);
        _sections.Add(section);
        _dirty = true;
    }

    public static void Unregister(string id) => _sections.RemoveAll(s => s.Id == id);

    public static IReadOnlyList<IDevMenuSection> Sections
    {
        get
        {
            if (_dirty)
            {
                _sections.Sort((a, b) => a.Order != b.Order
                    ? a.Order.CompareTo(b.Order)
                    : string.Compare(a.Title, b.Title, StringComparison.Ordinal));
                _dirty = false;
            }
            return _sections;
        }
    }
}
