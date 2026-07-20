using System.Collections.Immutable;

namespace WindowsMvp.Managed.Protocol;

public sealed record BuildMessage(string Name, ImmutableArray<string> Inputs);
