namespace StyloExtract.Abstractions;

public static class StyloExtractSignals
{
    public const string ParseDone               = "stylo.extract.parse.done";
    public const string FingerprintComputed     = "stylo.extract.fingerprint.computed";
    public const string MatchFastPathHit        = "stylo.extract.match.fastpath.hit";
    public const string MatchFastPathMiss       = "stylo.extract.match.fastpath.miss";
    public const string MatchSlowPathMatch      = "stylo.extract.match.slowpath.match";
    public const string MatchSlowPathMiss       = "stylo.extract.match.slowpath.miss";
    public const string TemplateNovel           = "stylo.extract.template.novel";
    public const string TemplateRefit           = "stylo.extract.template.refit";
    public const string ObservationRecorded     = "stylo.extract.observation.recorded";
    public const string DriftObserved           = "stylo.extract.drift.observed";
    public const string VersionDetected         = "stylo.extract.version.detected";
}
