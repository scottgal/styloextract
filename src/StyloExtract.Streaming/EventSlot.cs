namespace StyloExtract.Streaming;

public readonly struct EventSlot
{
    public readonly ulong TagHash;
    public readonly ulong ClassHash;
    public readonly ulong PrevTagHash;

    public EventSlot(ulong tagHash, ulong classHash) : this(tagHash, classHash, 0UL) { }

    public EventSlot(ulong tagHash, ulong classHash, ulong prevTagHash)
    {
        TagHash = tagHash;
        ClassHash = classHash;
        PrevTagHash = prevTagHash;
    }
}
