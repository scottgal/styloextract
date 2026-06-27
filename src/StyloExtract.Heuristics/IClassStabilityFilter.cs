namespace StyloExtract.Heuristics;

/// <summary>
/// Decides whether a class name (or id) is stable enough to anchor a selector.
/// Hash-shaped tokens (Tailwind JIT, CSS-modules, design-system hashes) get
/// rejected so the emitted claims don't churn on every site deploy.
/// </summary>
public interface IClassStabilityFilter
{
    bool IsStable(string token);
}
