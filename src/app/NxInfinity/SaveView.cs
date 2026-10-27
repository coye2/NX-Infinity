using NxInfinity.Runtime;
using NxInfinity.Runtime.Services;

namespace NxInfinity;

public sealed record SaveView(NativeSaveDescriptor Native, SaveDirectoryMapping Mapping, string BackendRevision);
