namespace StyloExtract.Abstractions;

/// <summary>
/// A minimal byte-level pattern that matches an HTML tag with attribute-order,
/// quote-style, and whitespace tolerance.
///
/// Built by the streaming inducer (Task 13 of Phase 1) from an example page's
/// chosen marker element. The hot-path scanner walks raw response bytes and
/// fires this pattern against them — no tokeniser, no DOM materialisation.
///
/// The match contract: a window of bytes matches when (a) it opens with
/// <c>&lt;</c> + <see cref="TagName"/> (or <c>&lt;/</c> + <see cref="TagName"/>
/// when <see cref="IsClose"/>), (b) for every <see cref="AttrConstraint"/> in
/// <see cref="Attrs"/> the attribute appears anywhere inside the tag (any
/// order) with the required value (quoted, single-quoted, or unquoted), and
/// (c) the closing <c>&gt;</c> appears within <see cref="MaxScanBytes"/> of
/// the tag-name start.
///
/// Backing storage uses arrays (not <c>ReadOnlySpan&lt;byte&gt;</c>) so the
/// type can be a record, serialised, and reused across requests. The hot path
/// reads through <see cref="TagNameSpan"/> / <see cref="AttrsSpan"/> to keep
/// per-match work span-based.
/// </summary>
public sealed record BytePattern
{
    /// <summary>
    /// Tag name as UTF-8 bytes, lowercase. E.g. <c>"main"u8.ToArray()</c>.
    /// Required and non-empty.
    /// </summary>
    public required byte[] TagName { get; init; }

    /// <summary>
    /// Required attribute constraints. Each must be satisfied for the pattern
    /// to match; order in the input tag is irrelevant. Empty array means
    /// "match any element with this tag name".
    /// </summary>
    public AttrConstraint[] Attrs { get; init; } = Array.Empty<AttrConstraint>();

    /// <summary>
    /// True if this pattern targets a close tag (<c>&lt;/tag&gt;</c>). Close
    /// tags carry no attributes, so <see cref="Attrs"/> must be empty when
    /// this is true.
    /// </summary>
    public bool IsClose { get; init; }

    /// <summary>
    /// Maximum bytes the matcher scans past the tag-name start while hunting
    /// for the closing <c>&gt;</c> and any required attributes. Caps the
    /// per-tag worst case for pathological open tags. Defaults to 256.
    /// </summary>
    public int MaxScanBytes { get; init; } = 256;

    /// <summary>Span view over <see cref="TagName"/> for hot-path matching.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlySpan<byte> TagNameSpan => TagName;

    /// <summary>Span view over <see cref="Attrs"/> for hot-path matching.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlySpan<AttrConstraint> AttrsSpan => Attrs;
}

/// <summary>
/// One required (name, value) attribute pair on a <see cref="BytePattern"/>.
/// The matcher accepts the value in any of the three HTML5 attribute forms:
/// <c>name="value"</c>, <c>name='value'</c>, or unquoted <c>name=value</c>
/// (HTML5 allows unquoted when the value contains no whitespace or special
/// characters).
/// </summary>
public sealed record AttrConstraint
{
    /// <summary>Attribute name as UTF-8 bytes, lowercase. Required.</summary>
    public required byte[] Name { get; init; }

    /// <summary>Required attribute value as UTF-8 bytes, verbatim. Required.</summary>
    public required byte[] Value { get; init; }

    /// <summary>Span view over <see cref="Name"/> for hot-path matching.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlySpan<byte> NameSpan => Name;

    /// <summary>Span view over <see cref="Value"/> for hot-path matching.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ReadOnlySpan<byte> ValueSpan => Value;
}
