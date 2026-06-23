namespace StyloExtract.Ml.Features;

/// <summary>
/// Stable index assignment for the per-element feature vector. The order is
/// load-bearing: the offline training pipeline writes features in this order;
/// the ONNX model expects them in this order. Bump <see cref="Version"/> when
/// the layout changes and re-train.
///
/// <para>
/// All offsets are constants so the inner extraction loop hand-indexes into
/// <c>Span&lt;float&gt;</c> without any string lookups or dictionary work.
/// <see cref="Names"/> is debug-only and shouldn't be referenced from the
/// hot path.
/// </para>
/// </summary>
public static class FeatureNames
{
    public const int Version = 1;

    // ----- Tag identity (one-hot, 12 dims) -----
    public const int TagMain          = 0;
    public const int TagArticle       = 1;
    public const int TagSection       = 2;
    public const int TagAside         = 3;
    public const int TagNav           = 4;
    public const int TagHeader        = 5;
    public const int TagFooter        = 6;
    public const int TagForm          = 7;
    public const int TagTable         = 8;
    public const int TagPre           = 9;
    public const int TagDiv           = 10;
    public const int TagOther         = 11;

    // ----- Class-name hash buckets (8 dims) -----
    public const int ClassBucket0     = 12;
    public const int ClassBucket1     = 13;
    public const int ClassBucket2     = 14;
    public const int ClassBucket3     = 15;
    public const int ClassBucket4     = 16;
    public const int ClassBucket5     = 17;
    public const int ClassBucket6     = 18;
    public const int ClassBucket7     = 19;

    // ----- Density / text features (10 dims) -----
    public const int LogTextLength    = 20;
    public const int LinkDensity      = 21;
    public const int ImageDensity     = 22;
    public const int LogWordCount     = 23;
    public const int LogHeadingCount  = 24;
    public const int LogParagraphCount= 25;
    public const int LogListItemCount = 26;
    public const int ParaToHeadingRatio = 27;
    public const int InputCount       = 28;
    public const int ButtonCount      = 29;

    // ----- Position features (5 dims) -----
    public const int Depth            = 30;
    public const int PositionFromStart= 31;
    public const int PositionFromEnd  = 32;
    public const int ParentChildCount = 33;
    public const int SiblingTextFraction = 34;

    // ----- Sibling-shape features (5 dims) -----
    public const int RepeatedSiblingCount = 35;
    public const int RepeatedShapeScore   = 36;
    public const int SiblingTagEntropy    = 37;
    public const int IsLargestSiblingByText = 38;
    public const int SiblingsWithDescendantText = 39;

    // ----- Ancestor presence (5 dims) -----
    public const int AncestorMain     = 40;
    public const int AncestorArticle  = 41;
    public const int AncestorNav      = 42;
    public const int AncestorForm     = 43;
    public const int AncestorAside    = 44;

    /// <summary>Total dimension count of the feature vector.</summary>
    public const int Dim = 45;

    /// <summary>
    /// Debug-only display names. Indexed by the feature offset. Useful for
    /// dumping feature vectors as labelled key-value pairs in tests and
    /// in the `--ml --explain` CLI surface. Not allocated on the hot path.
    /// </summary>
    public static readonly string[] Names = new string[Dim]
    {
        "tag_main", "tag_article", "tag_section", "tag_aside",
        "tag_nav", "tag_header", "tag_footer", "tag_form",
        "tag_table", "tag_pre", "tag_div", "tag_other",
        "class_bucket0", "class_bucket1", "class_bucket2", "class_bucket3",
        "class_bucket4", "class_bucket5", "class_bucket6", "class_bucket7",
        "log_text_length", "link_density", "image_density", "log_word_count",
        "log_heading_count", "log_paragraph_count", "log_list_item_count",
        "para_to_heading_ratio", "input_count", "button_count",
        "depth", "position_from_start", "position_from_end",
        "parent_child_count", "sibling_text_fraction",
        "repeated_sibling_count", "repeated_shape_score", "sibling_tag_entropy",
        "is_largest_sibling_by_text", "siblings_with_descendant_text",
        "ancestor_main", "ancestor_article", "ancestor_nav", "ancestor_form",
        "ancestor_aside",
    };
}
