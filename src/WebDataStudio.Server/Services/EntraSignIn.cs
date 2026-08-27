using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using Microsoft.Data.SqlClient;

namespace WebDataStudio.Server.Services;

/// The code a person types somewhere else, as this studio passes it around: Azure.Identity's own
/// shape stops at the edge, so the flow can be exercised without a tenant.
public sealed record DeviceCode(
    string UserCode, string VerificationUrl, string? Message, DateTimeOffset ExpiresOn);

/// Where a sign-in has got to. The code and the URL are what the person needs; the token is never
/// part of this.
public sealed record EntraStatus(
    string State,
    string? UserCode,
    string? VerificationUrl,
    string? Message,
    DateTimeOffset? ExpiresOn,
    string? Error);

/// Signing in to Azure SQL, Synapse or Fabric as a person rather than as the machine.
///
/// A managed identity needs none of this and is the better answer where it exists. This is for the
/// other case: somebody's own account, from a studio running in a container where no browser can be
/// opened. The device-code flow is the one that works there — the studio shows a code, the person
/// enters it on a device that does have a browser, and the token arrives here.
///
/// The token is held in memory, per connection, until it expires. It is never written to disk, never
/// logged and never sent to the browser: the browser only ever sees the code and the URL.
public sealed class EntraSignIn(ILogger<EntraSignIn> log)
{
    /// What Azure SQL, Synapse and Fabric all authenticate against.
    public const string DatabaseScope = "https://database.windows.net/.default";

    /// The Azure CLI's own client id. A public client that is already consented to in every tenant,
    /// which is what makes a device-code flow work without registering an application first.
    private const string DefaultClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    private sealed record Pending(EntraStatus Status, AccessToken? Token);

    private readonly ConcurrentDictionary<string, Pending> _state = new();

    /// Overridable so the flow can be exercised without a tenant: a test hands in a credential that
    /// invokes the callback and returns a token.
    public Func<string, Func<DeviceCode, Task>, TokenCredential> CredentialFactory
    { get; init; } = (tenantId, report) => new DeviceCodeCredential(new DeviceCodeCredentialOptions
    {
        ClientId = DefaultClientId,
        TenantId = string.IsNullOrWhiteSpace(tenantId) ? "organizations" : tenantId,
        DeviceCodeCallback = (info, _) => report(new DeviceCode(info.UserCode,
            info.VerificationUri.ToString(), info.Message, info.ExpiresOn)),
        // Nothing is cached on disk: a token cache file in a container is a token somebody else can
        // read out of a volume.
        DisableAutomaticAuthentication = false,
    });

    /// Starts a sign-in and returns at once, before the person has typed the code anywhere. The
    /// browser polls Status for the code and then for the outcome.
    public EntraStatus Start(string connectionId, string? tenantId, CancellationToken ct)
    {
        if (_state.TryGetValue(connectionId, out var existing)
            && existing.Status.State == "pending")
            return existing.Status;

        _state[connectionId] = new Pending(
            new EntraStatus("starting", null, null, null, null, null), null);

        var credential = CredentialFactory(tenantId ?? "", code =>
        {
            _state[connectionId] = new Pending(new EntraStatus("pending", code.UserCode,
                code.VerificationUrl, code.Message, code.ExpiresOn, null), null);

            return Task.CompletedTask;
        });

        // Deliberately not awaited: the request that started this returns immediately, and the flow
        // lives as long as the person needs to walk to another device.
        _ = Task.Run(async () =>
        {
            try
            {
                var token = await credential.GetTokenAsync(
                    new TokenRequestContext([DatabaseScope]), CancellationToken.None);

                _state[connectionId] = new Pending(
                    new EntraStatus("signed-in", null, null, null, token.ExpiresOn, null), token);
            }
            catch (Exception e)
            {
                // The message is the useful part — expired code, wrong tenant, consent missing.
                log.LogWarning("Entra sign-in for {Connection} failed: {Message}", connectionId, e.Message);
                _state[connectionId] = new Pending(
                    new EntraStatus("failed", null, null, null, null, e.Message), null);
            }
        }, CancellationToken.None);

        return _state[connectionId].Status;
    }

    public EntraStatus Status(string connectionId)
    {
        if (!_state.TryGetValue(connectionId, out var pending))
            return new EntraStatus("none", null, null, null, null, null);

        // An expired token is not a sign-in: say so rather than letting the next open fail.
        if (pending.Token is { } token && token.ExpiresOn <= DateTimeOffset.UtcNow.AddMinutes(1))
        {
            _state.TryRemove(connectionId, out _);
            return new EntraStatus("expired", null, null, null, token.ExpiresOn, null);
        }

        return pending.Status;
    }

    /// Forgets the token. The next open asks for a code again.
    public void SignOut(string connectionId) => _state.TryRemove(connectionId, out _);

    /// The token for this connection, or null when nobody has signed in or it has expired.
    public string? TokenFor(string connectionId) =>
        _state.TryGetValue(connectionId, out var pending)
        && pending.Token is { } token
        && token.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(1)
            ? token.Token
            : null;
}

/// The two things a connection string can say that mean "a person signs in, interactively".
///
/// SqlClient can do both itself, and in a container neither works: interactive opens a browser on
/// the server, and its device-code callback writes to the server's console. The studio therefore
/// takes the flow over — <see cref="EntraSignIn"/> gets the code in front of the person — and hands
/// SqlClient the token it ended up with.
public static class EntraConnectionString
{
    public static bool WantsAPerson(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);

            return builder.Authentication is SqlAuthenticationMethod.ActiveDirectoryInteractive
                or SqlAuthenticationMethod.ActiveDirectoryDeviceCodeFlow;
        }
        catch (ArgumentException)
        {
            // Not a SQL Server connection string at all.
            return false;
        }
    }

    /// The same connection string with the authentication method taken out, because an access token
    /// and an Authentication= keyword cannot be used together.
    public static string WithoutAuthentication(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        builder.Remove("Authentication");
        builder.Remove("User ID");
        builder.Remove("Password");

        return builder.ConnectionString;
    }
}
