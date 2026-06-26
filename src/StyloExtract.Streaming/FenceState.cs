namespace StyloExtract.Streaming;

public enum FenceState : byte
{
    AwaitPrefix,
    AwaitContentStart,
    Capturing,
    Captured,
    Bailed,
}
