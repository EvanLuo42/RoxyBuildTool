using System.Collections.Immutable;
using WindowsMvp.Managed.Protocol;

var message = new BuildMessage("managed-tool", ImmutableArray.Create("EngineCore"));
Console.WriteLine($"{message.Name}: {string.Join(',', message.Inputs)}");
