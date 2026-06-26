namespace StyloExtract.Streaming;

public enum ScanVerdict : byte
{
    Continue,
    Captured,
    Bailout,
}
