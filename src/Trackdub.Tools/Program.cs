namespace Trackdub.Tools;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationTokenSource.Cancel();
        };

        Console.CancelKeyPress += handler;
        try
        {
            return await RunAsync(
                args,
                Console.Out,
                Console.Error,
                cancellationTokenSource.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
            cancellationTokenSource.Dispose();
        }
    }

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        string[] effectiveArgs;
        if (args.Length == 0)
        {
            effectiveArgs = args;
        }
        else if (string.Equals(args[0], "ingest", StringComparison.OrdinalIgnoreCase))
        {
            effectiveArgs = args[1..];
        }
        else if (string.Equals(args[0], "stem-lab", StringComparison.OrdinalIgnoreCase))
        {
            return await StemLabCommand.RunAsync(args[1..], output, error, cancellationToken).ConfigureAwait(false);
        }
        else if (string.Equals(args[0], "model-lab", StringComparison.OrdinalIgnoreCase))
        {
            return await ModelLabCommand.RunAsync(args[1..], output, error, cancellationToken).ConfigureAwait(false);
        }
        else if (args[0].StartsWith('-'))
        {
            effectiveArgs = args;
        }
        else
        {
            error.WriteLine($"Unknown command '{args[0]}'.");
            WriteUsage(error);
            return 1;
        }

        return await MediaIngestCommand.RunAsync(effectiveArgs, output, error, cancellationToken).ConfigureAwait(false);
    }

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Trackdub.Tools");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  ingest     Create or inspect a .trackdub project from source media.");
        writer.WriteLine("  stem-lab   Run a standalone external stem separation harness.");
        writer.WriteLine("  model-lab  Build, Olive-optimize, benchmark, and manifest ORT GenAI variants.");
        writer.WriteLine();
        writer.WriteLine("Run 'Trackdub.Tools <command> --help' for command-specific options.");
    }
}
