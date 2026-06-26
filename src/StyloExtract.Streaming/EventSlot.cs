namespace StyloExtract.Streaming;

public readonly struct EventSlot
{
    public readonly ulong TagHash;
    public readonly ulong ClassHash;

    public EventSlot(ulong tagHash, ulong classHash)
    {
        TagHash = tagHash;
        ClassHash = classHash;
    }
}
