namespace StyloExtract.Templates;

public static class AgingPriorityScorer
{
    public static double Score(
        double similarity,
        int totalObservationCount,
        double ageDaysSinceLastSeen,
        double lambdaObs = 0.02,
        double lambdaRecent = 0.05,
        double tauDays = 30.0)
    {
        var obsBonus = lambdaObs * Math.Log(1 + totalObservationCount);
        var recentBonus = lambdaRecent * Math.Exp(-ageDaysSinceLastSeen / tauDays);
        return similarity + obsBonus + recentBonus;
    }
}
