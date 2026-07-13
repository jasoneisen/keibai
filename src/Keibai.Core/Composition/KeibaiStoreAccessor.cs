using Keibai.Core.Bit;
using Marten;

namespace Keibai.Core.Composition;

/// <summary>Adapts the ancillary <see cref="IKeibaiStore"/> to the host-agnostic <see cref="IKeibaiStoreAccessor"/>.</summary>
public sealed class KeibaiStoreAccessor(IKeibaiStore store) : IKeibaiStoreAccessor
{
    /// <inheritdoc/>
    public IDocumentSession LightweightSession() => store.LightweightSession();

    /// <inheritdoc/>
    public IQuerySession QuerySession() => store.QuerySession();
}
