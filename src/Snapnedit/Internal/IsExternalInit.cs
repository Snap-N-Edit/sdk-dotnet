#if NETSTANDARD2_0
using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// Compiler plumbing for <c>init</c> accessors and <c>record</c> types, which
/// netstandard2.0's own reference assemblies do not carry. Never referenced by
/// hand.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
#endif
