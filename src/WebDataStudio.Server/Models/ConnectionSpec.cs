namespace WebDataStudio.Server.Models;

public enum ConnectionSource { Environment, Stored }

/// An SSH jump host in front of a database that is not reachable directly. Either a password or a
/// private key, never both — the form offers one or the other.
public sealed record TunnelSpec(
    string Host,
    int Port,
    string User,
    string? Password = null,
    string? PrivateKey = null,
    string? Passphrase = null);

/// A connection as the server knows it, including its secret. Never serialise this to a client.
public sealed record ConnectionSpec(
    string Id,
    string Name,
    string Engine,
    string ConnectionString,
    bool ReadOnly,
    string? Color,
    string? Group,
    ConnectionSource Source,
    TunnelSpec? Tunnel = null);

/// The client-facing shape: no connection string, no password, just enough to identify the target.
public sealed record ConnectionDto(
    string Id,
    string Name,
    string Engine,
    bool ReadOnly,
    string? Color,
    string? Group,
    string Source,
    string Summary,
    bool Tunnelled = false);
