namespace VisualBuffer.Services.Clipboard;

public sealed class ClipboardCache
{
    public static ClipboardCache Instance { get; } = new();
    private readonly LinkedList<string> _items = new();
    private readonly int _limit = 500;

    public string? LastText { get; private set; }

    public void SetLast(string text)
    {
        LastText = text;
        _items.AddFirst(text);
        while (_items.Count > _limit) _items.RemoveLast();
    }

    public IEnumerable<string> TakeLast(int n) => _items.Take(n);
}
