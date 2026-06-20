namespace StyloExtract.Fingerprint;

public static class JaccardEstimator
{
    public static double Estimate(uint[] a, uint[] b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Signatures must be equal length.");
        int matches = 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) matches++;
        }
        return (double)matches / a.Length;
    }
}
