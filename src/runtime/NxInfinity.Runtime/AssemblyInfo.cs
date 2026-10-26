using System.Runtime.CompilerServices;

// The enhancement module is part of the SDK. Applications and UI clients use its
// public coordinator; they never receive the private core or transaction lease.
[assembly: InternalsVisibleTo("NxInfinity.Runtime.Enhancements")]
// Focused real-pipe tests exercise framing lifetime and UI-context independence.
[assembly: InternalsVisibleTo("NxInfinity.Runtime.Transport.Performance.Tests")]
