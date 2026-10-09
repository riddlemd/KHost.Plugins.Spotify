using KHost.Plugins.Spotify.Control;
using Microsoft.Extensions.Logging;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>The record in memory, with a hook that runs at each write so a test can see what the
/// room was hearing at that moment.</summary>
internal sealed class FakePendingFadeLevels : IPendingFadeLevels
{
    public Dictionary<string, double> Levels { get; } = new(StringComparer.Ordinal);

    public int Writes { get; private set; }

    public Action? OnWrite { get; set; }

    public bool WriteThrows { get; set; }

    public IReadOnlyDictionary<string, double> Read() => Levels
        .Where(entry => PendingFadeLevelFile.IsRestorable(entry.Value))
        .ToDictionary(entry => entry.Key, entry => entry.Value);

    public void Write(IReadOnlyDictionary<string, double> levels)
    {
        if (WriteThrows)
            throw new IOException("disk full");

        Writes++;
        OnWrite?.Invoke();

        Levels.Clear();

        foreach (var (key, level) in levels)
            Levels[key] = level;
    }
}

internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception)));
}

internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly ListLogger _inner = new();

    public List<(LogLevel Level, string Message)> Entries => _inner.Entries;

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _inner.Log(logLevel, eventId, state, exception, formatter);
}
