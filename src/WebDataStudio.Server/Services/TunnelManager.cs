using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using ConnectionInfo = Renci.SshNet.ConnectionInfo;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

public sealed class SshTunnelException(string message, Exception? inner = null)
    : Exception(message, inner);

/// One live SSH connection with one local forward on it. The manager owns the lifetime; nothing
/// else creates these.
internal sealed class SshTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly ForwardedPortLocal _port;

    public string Host => "127.0.0.1";
    public int LocalPort => (int)_port.BoundPort;
    public int References { get; set; }
    public DateTimeOffset IdleSince { get; set; } = DateTimeOffset.UtcNow;

    private SshTunnel(SshClient client, ForwardedPortLocal port)
    {
        _client = client;
        _port = port;
    }

    public bool Alive => _client.IsConnected && _port.IsStarted;

    public static SshTunnel Open(TunnelSpec spec, string remoteHost, int remotePort)
    {
        var info = Authentication(spec);

        // Keep-alive so a firewall's idle timeout does not silently kill a tunnel that a long
        // query is still using.
        info.Timeout = TimeSpan.FromSeconds(20);

        var client = new SshClient(info);
        client.KeepAliveInterval = TimeSpan.FromSeconds(30);

        try
        {
            client.Connect();

            // Port 0 lets the OS pick a free local port; a fixed one would collide across tunnels.
            var port = new ForwardedPortLocal("127.0.0.1", 0, remoteHost, (uint)remotePort);
            client.AddForwardedPort(port);
            port.Start();

            return new SshTunnel(client, port);
        }
        catch (Exception e)
        {
            client.Dispose();

            // Naming SSH here is the whole point: without it the caller sees a generic timeout
            // against a host that was never reachable directly in the first place.
            throw new SshTunnelException(
                $"the SSH tunnel to {spec.User}@{spec.Host}:{spec.Port} could not be opened: {e.Message}", e);
        }
    }

    private static ConnectionInfo Authentication(TunnelSpec spec)
    {
        if (spec.PrivateKey is { Length: > 0 })
        {
            try
            {
                using var key = new MemoryStream(Encoding.UTF8.GetBytes(spec.PrivateKey));
                var file = spec.Passphrase is { Length: > 0 }
                    ? new PrivateKeyFile(key, spec.Passphrase)
                    : new PrivateKeyFile(key);

                return new ConnectionInfo(spec.Host, spec.Port, spec.User,
                    new PrivateKeyAuthenticationMethod(spec.User, file));
            }
            catch (SshException e)
            {
                throw new SshTunnelException($"the SSH private key could not be read: {e.Message}", e);
            }
        }

        return new ConnectionInfo(spec.Host, spec.Port, spec.User,
            new PasswordAuthenticationMethod(spec.User, spec.Password ?? ""));
    }

    public void Dispose()
    {
        try { _port.Stop(); } catch (Exception) { /* already down */ }
        _port.Dispose();
        _client.Dispose();
    }
}

/// Opens at most one tunnel per distinct tunnel spec and target, reference-counted so concurrent
/// sessions share it. A tunnel outlives its last session by a grace period, because closing and
/// reopening on every query would cost an SSH handshake each time.
public sealed class TunnelManager : IDisposable
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, SshTunnel> _tunnels = new();
    private readonly Lock _gate = new();
    private readonly Timer _sweeper;

    public TunnelManager()
    {
        _sweeper = new Timer(_ => Sweep(), null, Grace, Grace);
    }

    /// The local endpoint the driver should connect to instead of the real one.
    public (string Host, int Port) Ensure(TunnelSpec spec, string remoteHost, int remotePort)
    {
        var key = Key(spec, remoteHost, remotePort);

        lock (_gate)
        {
            if (_tunnels.TryGetValue(key, out var existing))
            {
                // A tunnel whose SSH connection dropped is worse than none: replace it.
                if (existing.Alive)
                {
                    existing.References++;
                    return (existing.Host, existing.LocalPort);
                }

                existing.Dispose();
                _tunnels.Remove(key);
            }

            var tunnel = SshTunnel.Open(spec, remoteHost, remotePort);
            tunnel.References = 1;
            _tunnels[key] = tunnel;
            return (tunnel.Host, tunnel.LocalPort);
        }
    }

    public void Release(TunnelSpec spec, string remoteHost, int remotePort)
    {
        var key = Key(spec, remoteHost, remotePort);

        lock (_gate)
        {
            if (!_tunnels.TryGetValue(key, out var tunnel)) return;

            tunnel.References = Math.Max(0, tunnel.References - 1);
            if (tunnel.References == 0) tunnel.IdleSince = DateTimeOffset.UtcNow;
        }
    }

    public int LiveCount { get { lock (_gate) return _tunnels.Count; } }

    private void Sweep()
    {
        lock (_gate)
        {
            foreach (var (key, tunnel) in _tunnels.ToList())
            {
                if (tunnel.References > 0 && tunnel.Alive) continue;
                if (tunnel.References > 0) { /* dead but still referenced: drop it anyway */ }
                else if (DateTimeOffset.UtcNow - tunnel.IdleSince < Grace) continue;

                tunnel.Dispose();
                _tunnels.Remove(key);
            }
        }
    }

    /// Hashed rather than concatenated: the key must not carry a password or a private key around
    /// in a dictionary that a memory dump would show plainly.
    private static string Key(TunnelSpec spec, string remoteHost, int remotePort)
    {
        var material = JsonSerializer.Serialize(spec) + $"|{remoteHost}|{remotePort}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public void Dispose()
    {
        _sweeper.Dispose();

        lock (_gate)
        {
            foreach (var tunnel in _tunnels.Values) tunnel.Dispose();
            _tunnels.Clear();
        }
    }
}
