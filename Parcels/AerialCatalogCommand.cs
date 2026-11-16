namespace IsSeattleFull.Parcels;

public static class AerialCatalogCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var data = Path.GetFullPath("data");
            var output = Path.GetFullPath("exports");
            var refresh = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--refresh") { refresh = true; continue; }
                if (args[i] is "--help" or "-h")
                {
                    Console.WriteLine("dotnet run -- catalog [--data-dir PATH] [--output-dir PATH] [--refresh]");
                    return 0;
                }
                var option = args[i];
                if (option is not ("--data-dir" or "--output-dir") || ++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("Use catalog --help for supported arguments.");
                if (option == "--data-dir") data = Path.GetFullPath(args[i]);
                else output = Path.GetFullPath(args[i]);
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            await AerialCatalog.BuildAsync(new DatasetCache(http, Console.Out), data, output, refresh, Console.Out, cancellation.Token);
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Aerial catalog preparation canceled; completed input snapshots remain cached.");
            return 130;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or HttpRequestException or
            System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or NetTopologySuite.Geometries.TopologyException)
        {
            Console.Error.WriteLine($"Aerial catalog preparation failed: {ex.Message}");
            return 1;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
