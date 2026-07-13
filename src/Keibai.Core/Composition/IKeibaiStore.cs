using Marten;

namespace Keibai.Core.Composition;

/// <summary>
/// Marker interface for Keibai's **ancillary** Marten store (schema <c>keibai</c>). Registered via
/// <c>AddMartenStore&lt;IKeibaiStore&gt;(...)</c> so it coexists with the OMD host's default store
/// after the merge. Inject <c>IKeibaiStore</c> — never a bare <c>IDocumentSession</c>.
/// </summary>
public interface IKeibaiStore : IDocumentStore;
