namespace BookMarker.Tests;

/// <summary>
/// Sets process environment variables and restores them on dispose.
/// </summary>
internal sealed class EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _original = [];

    public EnvScope Set(string key, string? value)
    {
        _original.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var (key, value) in _original)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
