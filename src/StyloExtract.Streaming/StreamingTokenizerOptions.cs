namespace StyloExtract.Streaming;

/// <summary>
/// Per-instance limits for the streaming tokenizer + byte-pattern scanner.
/// Replaces the legacy hard <c>const int MaxBufferSize</c> values that lived
/// on <c>IncrementalHtmlTokenizer</c> / <c>IncrementalBytePatternScanner</c>
/// and the <c>TagEvent.MaxClassesPerEvent</c> / <c>MaxAttrPairsPerEvent</c>
/// internal consts on <c>TagEvent</c>.
///
/// <para>Defaults are picked to never silently truncate on any HTML observed
/// in dogfood (BBC News + Guardian + JSON-LD-heavy + Tailwind-heavy SPAs).
/// The buffer caps exist as a sanity ceiling against truly hostile input
/// (e.g. a single 100 MiB script tag) — the streaming gateway is an
/// in-process system and could grow without bound, but a configurable
/// ceiling lets a host operator decide what counts as hostile.</para>
///
/// <para>Buffers are rented from <see cref="System.Buffers.ArrayPool{T}"/>
/// — the per-instance allocator is the shared pool, not <c>new byte[]</c>,
/// so growth doesn't churn the GC. Hot-path steady state allocates nothing
/// once a tokenizer has rented up to its high-water mark. The per-event
/// class / data-attr / aria-attr scratch buffers stay on the stack at the
/// configured limit; their cap exists only to bound a single
/// <c>stackalloc</c>.</para>
/// </summary>
public sealed record StreamingTokenizerOptions
{
    /// <summary>
    /// Sanity ceiling on the tokenizer's partial-tag buffer.
    /// <see cref="IncrementalHtmlTokenizer.Feed"/> throws
    /// <see cref="System.InvalidOperationException"/> if a single tag (or a
    /// script/style body without its closing marker) would push the buffer
    /// past this. Default: 1 MiB — far above any observed real-world tag,
    /// below any plausible hostile-input ceiling.
    /// </summary>
    public int MaxPartialTagBytes { get; init; } = 1 * 1024 * 1024;

    /// <summary>
    /// Sanity ceiling on the byte-pattern scanner's carry-over buffer.
    /// Mirrors <see cref="MaxPartialTagBytes"/> for the carry the scanner
    /// holds between chunks (longest pattern MaxScanBytes + the longest
    /// close-marker fragment). Default: 1 MiB.
    /// </summary>
    public int MaxCarryBufferBytes { get; init; } = 1 * 1024 * 1024;

    /// <summary>
    /// Maximum number of class tokens extracted per tag event. Real pages
    /// can carry &gt; 20 utility classes on a single element (Tailwind / BEM
    /// stacks); the previous cap of 8 silently dropped the tail and broke
    /// identity-claim matches that referenced a tail class. Default: 32.
    /// The parser stackallocs the configured amount; values above
    /// <see cref="StackallocClassCeiling"/> are rejected at construction
    /// to bound the per-tag stack draw.
    /// </summary>
    public int MaxClassesPerEvent { get; init; } = 32;

    /// <summary>
    /// Maximum number of <c>data-*</c> / <c>aria-*</c> attribute pairs
    /// extracted per tag event. Previous cap of 3 silently dropped pages
    /// that carried more (charting widgets, react-aria components). Default:
    /// 16. Values above <see cref="StackallocAttrCeiling"/> are rejected at
    /// construction.
    /// </summary>
    public int MaxAttrPairsPerEvent { get; init; } = 16;

    /// <summary>
    /// Hard ceiling for <see cref="MaxClassesPerEvent"/>. At 256 the
    /// parser's stackalloc draw is 256 * 8 = 2 KiB per call — comfortable
    /// within a 1 MiB thread stack.
    /// </summary>
    public const int StackallocClassCeiling = 256;

    /// <summary>
    /// Hard ceiling for <see cref="MaxAttrPairsPerEvent"/>. At 128 the
    /// stackalloc draw is 128 * 16 = 2 KiB per buffer (two buffers — data
    /// + aria).
    /// </summary>
    public const int StackallocAttrCeiling = 128;

    public static StreamingTokenizerOptions Default { get; } = new();

    /// <summary>
    /// Validate ranges; called at the consumer (tokenizer constructor) so
    /// invalid options surface at the obvious site, not deep in the hot
    /// path. Throws <see cref="System.ArgumentOutOfRangeException"/> when
    /// any cap is non-positive or above the stackalloc ceiling.
    /// </summary>
    internal void Validate()
    {
        if (MaxPartialTagBytes <= 0)
            throw new System.ArgumentOutOfRangeException(nameof(MaxPartialTagBytes), MaxPartialTagBytes, "must be > 0");
        if (MaxCarryBufferBytes <= 0)
            throw new System.ArgumentOutOfRangeException(nameof(MaxCarryBufferBytes), MaxCarryBufferBytes, "must be > 0");
        if (MaxClassesPerEvent <= 0 || MaxClassesPerEvent > StackallocClassCeiling)
            throw new System.ArgumentOutOfRangeException(nameof(MaxClassesPerEvent), MaxClassesPerEvent,
                $"must be in [1, {StackallocClassCeiling}]");
        if (MaxAttrPairsPerEvent <= 0 || MaxAttrPairsPerEvent > StackallocAttrCeiling)
            throw new System.ArgumentOutOfRangeException(nameof(MaxAttrPairsPerEvent), MaxAttrPairsPerEvent,
                $"must be in [1, {StackallocAttrCeiling}]");
    }
}

/// <summary>
/// Cheap value-type wrapper that threads the per-event class / attr caps
/// from <see cref="StreamingTokenizerOptions"/> down to
/// <see cref="TagAttributeParser.ExtractIdentityHashes"/>. Avoids stashing
/// the whole options record on the hot path.
/// </summary>
public readonly struct TagAttrLimits
{
    public int MaxClassesPerEvent { get; }
    public int MaxAttrPairsPerEvent { get; }

    public TagAttrLimits(int maxClassesPerEvent, int maxAttrPairsPerEvent)
    {
        MaxClassesPerEvent = maxClassesPerEvent;
        MaxAttrPairsPerEvent = maxAttrPairsPerEvent;
    }

    public static TagAttrLimits Default { get; } = new(
        StreamingTokenizerOptions.Default.MaxClassesPerEvent,
        StreamingTokenizerOptions.Default.MaxAttrPairsPerEvent);
}