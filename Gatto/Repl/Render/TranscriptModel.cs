namespace Gatto.Repl.Render;

//the display history behind the viewport and persistence. items are appended only, and the open item grows in place as lines stream, closed at a block boundary
public sealed class TranscriptModel(string role)
{
    //the role that new assistant blocks are tinted under. a mid-session /role retints later output, while committed blocks keep the role they streamed with
    public string Role { get; set; } = role;

    private readonly List<TranscriptItem> _items = new();
    private List<string>? _openLines;   //the open item's RawLines, grown in place as lines stream
    private bool _openReasoning;
    private TranscriptItem? _openItem;
    private int _reasoningCount;   //how many reasoning blocks the session has had, the first 2 get the click teaser

    public IReadOnlyList<TranscriptItem> Items => _items;

    //the item still being streamed, null when nothing is open
    internal TranscriptItem? OpenItem => _openItem;

    //the starting Collapsed value for a new reasoning item, true under reasoning:collapsed and false under expanded or a passthrough summary
    public bool ReasoningCollapseDefault { get; set; } = true;

    //fires on every append and every extend of the open item, and the argument is the item that changed
    public event Action<TranscriptItem>? OnAppendedOrExtended;

    //fires when Reset clears the model, so subscribers drop their per-item state
    public event Action? OnReset;

    //fires when CloseOpen drops a whitespace-only prose block, the only removal site, so a live selection clears
    public event Action<TranscriptItem>? OnItemRemoved;

    //clear the whole transcript for a fresh session, closing the open item and telling subscribers through OnReset. the caller adds the new session's lead back
    public void Reset()
    {
        CloseOpen();
        _items.Clear();
        _reasoningCount = 0;   //a fresh session starts the count again, so the click teaser comes back
        OnReset?.Invoke();
    }

    //append one line to the open item, opening one if there is none or the mode flipped. the open item is in Items at once, so a streaming line draws as it arrives
    public void AppendOpenLine(string rawLine, bool reasoning)
    {
        if (_openItem is not null && _openReasoning != reasoning) CloseOpen();
        if (_openItem is null)
        {
            _openLines = new List<string> { rawLine };
            _openReasoning = reasoning;
            _openItem = reasoning
                //the first two reasoning blocks get a click teaser on the summary, so the affordance is discoverable
                ? new ReasoningItem(_openLines) { Collapsed = ReasoningCollapseDefault, ClickHint = ++_reasoningCount <= 2 }
                : new AssistantBlockItem(_openLines, Role);
            _openItem.LeadingBlank = _items.Count > 0;   //one blank separator before every item after the first
            _items.Add(_openItem);
        }
        else
        {
            _openLines!.Add(rawLine);
            _openItem!.MarkContentChanged();   //growing the item in place bumps Rev, so a selection over the streaming item clears
        }
        OnAppendedOrExtended?.Invoke(_openItem);
    }

    //seal the open item at a block boundary and do nothing when none is open
    public void CloseOpen()
    {
        //drop an all-whitespace prose block before it becomes an empty item whose blank row doubles with the next one's. reasoning and event items are never dropped
        if (_openItem is AssistantBlockItem && _openLines is { } lines && lines.All(string.IsNullOrWhiteSpace))
        {
            var removed = _openItem;
            _items.Remove(removed);
            OnItemRemoved?.Invoke(removed);   //removing an item shifts the indexes, so a live selection clears
        }
        _openItem = null;
        _openLines = null;
    }

    //seal the open item when it is a reasoning item, stamping Elapsed, setting Collapsed and firing OnAppendedOrExtended so LineIndex recounts the rows
    public void CloseOpenReasoning(TimeSpan elapsed, bool collapse)
    {
        if (_openItem is ReasoningItem ri)
        {
            ri.Elapsed = elapsed;
            if (!ri.UserToggled) ri.Collapsed = collapse;   //the config default applies only when the user hasn't toggled, so an expand sticks
            ri.Streaming = false;   //once closed, a collapsed item shows the 1-row summary
            OnAppendedOrExtended?.Invoke(ri);
        }
        CloseOpen();
    }

    //append a finished item, closing the open one first. an event item arrives with its After anchor already set by the caller
    public void Append(TranscriptItem item)
    {
        CloseOpen();
        item.LeadingBlank = _items.Count > 0;   //one blank separator before every item after the first
        _items.Add(item);
        OnAppendedOrExtended?.Invoke(item);
    }
}
