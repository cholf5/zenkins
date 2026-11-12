namespace Zenkins.Worker;

/// <summary>
/// Entry point for the Zenkins build worker: a plain console process that must never
/// depend on ASP.NET Core (it is published self-contained/trimmed).
/// </summary>
internal static class Program
{
    private const string ServerUrlVariable = "ZENKINS_SERVER_URL";

    private static int Main()
    {
        var serverUrl = Environment.GetEnvironmentVariable(ServerUrlVariable);

        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            Console.WriteLine(
                $"Zenkins Worker is not configured: set {ServerUrlVariable} to the Zenkins Server base URL.");
            Console.WriteLine("Nothing to do. Exiting.");
            return 0;
        }

        Console.WriteLine($"Zenkins Worker targeting {serverUrl}.");
        Console.WriteLine("Worker runtime is not implemented yet. Exiting.");
        return 0;
    }
}
