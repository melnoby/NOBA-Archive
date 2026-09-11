using NOBA.Archive.Cli.Commands;

namespace NOBA.Archive.Cli;

/// <summary>The process entry point. Everything testable lives in <see cref="CliApplication"/>.</summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };

        var app = new CliApplication(Console.Out, Console.Error, Console.In);
        var exitCode = await app.RunAsync(args, cancellation.Token).ConfigureAwait(false);
        return (int)exitCode;
    }
}
