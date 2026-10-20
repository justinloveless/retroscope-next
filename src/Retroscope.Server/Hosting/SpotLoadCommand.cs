namespace Retroscope.Server.Hosting;

public static class SpotLoadCommand {
    public static Task<int> RunAsync(WebApplication app, string[] args) {
        Console.Error.WriteLine("spot-load is not implemented yet");
        return Task.FromResult(2);
    }
}
