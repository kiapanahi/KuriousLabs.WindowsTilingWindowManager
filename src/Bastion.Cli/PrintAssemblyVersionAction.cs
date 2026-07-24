using System.CommandLine;
using System.CommandLine.Invocation;
using System.Reflection;

namespace Bastion.Cli;

/// <summary>Prints the MinVer-derived informational version. GitHub issue #48.</summary>
internal sealed class PrintAssemblyVersionAction : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        string version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "unknown";
        parseResult.InvocationConfiguration.Output.WriteLine(version);
        return 0;
    }
}
