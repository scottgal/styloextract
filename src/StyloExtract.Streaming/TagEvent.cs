namespace StyloExtract.Streaming;

public readonly record struct TagEvent(
    ulong TagNameHash,
    ulong ClassHash,
    int ByteLength,
    bool IsClose);
