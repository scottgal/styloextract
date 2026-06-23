"""Single source of truth for the contract between training and inference.

  * LABELS — the BlockRole enum, ordinal-aligned with the C# enum. The
    model's class label IS the ordinal; any drift between LABELS here and
    the C# BlockRole values silently miscalibrates the trained model.
    Bump FEATURE_VERSION below when the C# enum changes.
  * DIM — the 45-dimensional feature vector size. Matches C#'s
    FeatureNames.Dim and is asserted by the runtime against ONNX metadata
    at model load.
  * FEATURE_VERSION — bumped in lockstep with C#'s FeatureNames.Version
    when the feature schema changes. The runtime refuses to load a model
    whose stamped version disagrees with the compiled-in version.

All three training scripts import from this module so any change is one
edit away from being uniform across wcxb_to_features.py, pipeline.py,
and features_to_onnx.py.
"""

# Order MUST match the C# BlockRole enum
# (src/StyloExtract.Abstractions/BlockRole.cs). Adding a value here without
# updating the C# enum (or vice versa) re-indexes the class labels and
# breaks every existing trained model.
LABELS = [
    "Unknown",
    "MainContent",
    "Article",
    "Heading",
    "Summary",
    "PrimaryNavigation",
    "SecondaryNavigation",
    "Breadcrumb",
    "Sidebar",
    "RelatedLinks",
    "Footer",
    "Header",
    "Advertisement",
    "CookieBanner",
    "Form",
    "Table",
    "CodeBlock",
    "Boilerplate",
    "RepeatedItem",
]

LABEL_TO_ID = {label: i for i, label in enumerate(LABELS)}

# Mirrors FeatureNames.Dim in
# src/StyloExtract.Ml/Features/FeatureNames.cs.
DIM = 45

# Mirrors FeatureNames.Version in
# src/StyloExtract.Ml/Features/FeatureNames.cs.
FEATURE_VERSION = 1
