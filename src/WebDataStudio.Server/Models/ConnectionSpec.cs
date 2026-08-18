namespace WebDataStudio.Server.Models;

public enum ConnectionSource { Environment, Stored }

/// A connection as the server knows it, including its secret. Never serialise this to a client.
public sealed record ConnectionSpec(
    string Id,
    string Name,
    string Engine,
    string ConnectionString,
    bool ReadOnly,
    string? Color,
    string? Group,
    ConnectionSource Source);

/// The client-facing shape: no connection string, no password, just enough to identify the target.
public sealed record ConnectionDto(
    string Id,
    string Name,
    string Engine,
    bool ReadOnly,
    string? Color,
    string? Group,
    string Source,
    string Summary);
