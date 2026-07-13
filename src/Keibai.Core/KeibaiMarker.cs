namespace Keibai.Core;

/// <summary>
/// Marker type in <c>Keibai.Core</c> for Wolverine handler-assembly discovery. The OMD host merges
/// Keibai by calling <c>opts.Discovery.IncludeAssembly(typeof(KeibaiMarker).Assembly)</c>.
/// </summary>
public sealed class KeibaiMarker;
