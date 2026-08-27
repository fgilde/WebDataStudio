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
    TunnelSpec? Tunnel = null,
    /// An Entra access token for this connection, where a person signed in for it. Set by the
    /// session factory, used by the driver instead of the Authentication= keyword — the two cannot
    /// be given together. Never serialised anywhere.
    string? AccessToken = null);

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
    bool Tunnelled = false,
    /// This connection is one a person signs in to — an Entra device-code or interactive flow —
    /// rather than one the machine can open on its own. The UI offers the sign-in for it.
    bool Interactive = false);
