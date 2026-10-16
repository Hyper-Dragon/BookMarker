using Microsoft.AspNetCore.Mvc.Testing;

namespace BookMarker.Tests;

/// <summary>
/// Starts the real service pipeline against the marker stand-in, with a private scratch
/// root. This is the deployment-shaped test: the same Program.cs, the same environment
/// contract, the same process launches — only the engine is a stub, because the engine
/// needs a GPU and model weights that no test runner has.
/// </summary>
internal sealed class ServiceHost : IDisposable
{
    private readonly EnvScope _environment;
    private readonly WebApplicationFactory<Program> _factory;

    public ServiceHost(params (string Key, string? Value)[] settings)
    {
        ScratchRoot = Path.Combine(Path.GetTempPath(), "bookmarker-tests", Guid.NewGuid().ToString("N"));

        _environment = new EnvScope()
            // Keeps Program off its app.Urls branch: the test server does not listen.
            .Set("ASPNETCORE_URLS", "http://127.0.0.1:0")
            .Set("MARKER_BINARY", MarkerStubPath)
            .Set("SCRATCH_ROOT", ScratchRoot)
            .Set("MAX_UPLOAD_BYTES", "1048576")
            .Set("OPENAI_BASE_URL", null)
            .Set("OPENAI_MODEL", null)
            .Set("OPENAI_API_KEY", null)
            .Set("HF_HUB_OFFLINE", null);

        foreach (var (key, value) in settings)
        {
            _environment.Set(key, value);
        }

        _factory = new WebApplicationFactory<Program>();
        Client = _factory.CreateClient();
    }

    /// <summary>The marker stand-in, copied next to the test assembly by the project reference.</summary>
    public static string MarkerStubPath { get; } = PrepareMarkerStub();

    public HttpClient Client { get; }

    public string ScratchRoot { get; }

    private static string PrepareMarkerStub()
    {
        var path = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "marker.exe" : "marker");
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"The marker stand-in was not found at '{path}'. Build tests/MarkerStub (dotnet build) first.");
        }

        // The apphost should arrive executable; on a fresh Linux checkout it sometimes does
        // not, and "Permission denied" from a test host is a confusing way to find that out.
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(
                path,
                mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }

        return path;
    }

    public void Dispose()
    {
        Client.Dispose();
        _factory.Dispose();
        _environment.Dispose();

        try
        {
            if (Directory.Exists(ScratchRoot))
            {
                Directory.Delete(ScratchRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort: the container behaviour under test is that the *job* dir is gone
        }
    }
}
