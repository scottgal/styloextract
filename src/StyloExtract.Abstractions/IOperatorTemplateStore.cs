namespace StyloExtract.Abstractions;

/// <summary>
/// Read-mostly lookup of operator-authored templates keyed by host. Implementations
/// hold whatever backing storage they like (YAML files on disk, SQLite cache, etc.);
/// the layout extractor only consumes <see cref="TryGet(string, out OperatorTemplate)"/>
/// before fingerprinting an incoming request.
///
/// <para>
/// Reads are expected to be sub-microsecond — they sit on the fast path. Implementations
/// must keep a parsed in-memory map and reload it from authority storage out-of-band
/// (file watcher / explicit reload), not on every read.
/// </para>
/// </summary>
public interface IOperatorTemplateStore
{
    /// <summary>
    /// Look up the template for <paramref name="host"/>. Returns true and sets
    /// <paramref name="template"/> when present; returns false otherwise.
    /// Host matching is exact; subdomain matching is out of scope.
    /// </summary>
    bool TryGet(string host, out OperatorTemplate template);

    /// <summary>
    /// Enumerate every loaded template in host-alphabetical order. Used by the
    /// CLI <c>template list</c> command and the REST <c>GET /templates</c> endpoint.
    /// </summary>
    IReadOnlyList<OperatorTemplate> List();
}
