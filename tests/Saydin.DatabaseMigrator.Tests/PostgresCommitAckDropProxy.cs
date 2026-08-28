using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Saydin.DatabaseMigrator.Tests;

/// <summary>
/// Test-only PostgreSQL wire proxy. It forwards the migration-019 COMMIT to the
/// server, waits until the server emits CommandComplete("COMMIT"), then drops the
/// client side before forwarding that acknowledgement or ReadyForQuery.
/// </summary>
internal sealed class PostgresCommitAckDropProxy : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _backendHost;
    private readonly int _backendPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly Task _acceptLoop;
    private int _armed;
    private int _awaitingCommitAcknowledgement;
    private int _dropped;

    private PostgresCommitAckDropProxy(string backendHost, int backendPort)
    {
        _backendHost = backendHost;
        _backendPort = backendPort;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }
    public bool DroppedCommitAcknowledgement => Volatile.Read(ref _dropped) == 1;

    public static PostgresCommitAckDropProxy Start(string backendHost, int backendPort) =>
        new(backendHost, backendPort);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var connection = HandleConnectionAsync(client, _stop.Token);
                lock (_connections) _connections.Add(connection);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var backend = new TcpClient())
        {
            try
            {
                await backend.ConnectAsync(_backendHost, _backendPort, cancellationToken);
                await using var clientStream = client.GetStream();
                await using var backendStream = backend.GetStream();
                var clientToServer = PumpClientAsync(
                    clientStream, backendStream, client, backend, cancellationToken);
                var serverToClient = PumpServerAsync(
                    backendStream, clientStream, client, backend, cancellationToken);
                await Task.WhenAny(clientToServer, serverToClient);
                client.Close();
                backend.Close();
                try { await Task.WhenAll(clientToServer, serverToClient); }
                catch (Exception) when (!_stop.IsCancellationRequested) { }
            }
            catch (Exception) when (!_stop.IsCancellationRequested) { }
        }
    }

    private async Task PumpClientAsync(
        NetworkStream source,
        NetworkStream destination,
        TcpClient client,
        TcpClient backend,
        CancellationToken cancellationToken)
    {
        var startupLengthBytes = new byte[4];
        if (!await ReadExactlyOrEofAsync(source, startupLengthBytes, cancellationToken)) return;
        var startupLength = BinaryPrimitives.ReadInt32BigEndian(startupLengthBytes);
        if (startupLength is < 8 or > 1_048_576) throw new InvalidDataException("Invalid PostgreSQL startup packet.");
        var startupPayload = new byte[startupLength - 4];
        if (!await ReadExactlyOrEofAsync(source, startupPayload, cancellationToken)) return;
        await destination.WriteAsync(startupLengthBytes, cancellationToken);
        await destination.WriteAsync(startupPayload, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var type = source.ReadByte();
            if (type < 0) return;
            var lengthBytes = new byte[4];
            if (!await ReadExactlyOrEofAsync(source, lengthBytes, cancellationToken)) return;
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 4 or > 64 * 1024 * 1024)
                throw new InvalidDataException("Invalid PostgreSQL frontend frame.");
            var payload = new byte[length - 4];
            if (!await ReadExactlyOrEofAsync(source, payload, cancellationToken)) return;

            if (type is (byte)'Q' or (byte)'P')
            {
                var text = Encoding.UTF8.GetString(payload);
                if (text.Contains("CREATE TABLE public.saydin_role_contract", StringComparison.Ordinal))
                    Interlocked.Exchange(ref _armed, 1);
                if (Volatile.Read(ref _armed) == 1 &&
                    text.Contains("COMMIT", StringComparison.OrdinalIgnoreCase))
                    Interlocked.Exchange(ref _awaitingCommitAcknowledgement, 1);
            }

            await destination.WriteAsync(new byte[] { (byte)type }, cancellationToken);
            await destination.WriteAsync(lengthBytes, cancellationToken);
            await destination.WriteAsync(payload, cancellationToken);
        }
    }

    private async Task PumpServerAsync(
        NetworkStream source,
        NetworkStream destination,
        TcpClient client,
        TcpClient backend,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var type = source.ReadByte();
            if (type < 0) return;
            var lengthBytes = new byte[4];
            if (!await ReadExactlyOrEofAsync(source, lengthBytes, cancellationToken)) return;
            var length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
            if (length is < 4 or > 64 * 1024 * 1024)
                throw new InvalidDataException("Invalid PostgreSQL backend frame.");
            var payload = new byte[length - 4];
            if (!await ReadExactlyOrEofAsync(source, payload, cancellationToken)) return;

            if (type == (byte)'C' && Volatile.Read(ref _awaitingCommitAcknowledgement) == 1 &&
                Encoding.UTF8.GetString(payload).StartsWith("COMMIT", StringComparison.Ordinal) &&
                Interlocked.CompareExchange(ref _dropped, 1, 0) == 0)
            {
                client.Close();
                backend.Close();
                return;
            }

            await destination.WriteAsync(new byte[] { (byte)type }, cancellationToken);
            await destination.WriteAsync(lengthBytes, cancellationToken);
            await destination.WriteAsync(payload, cancellationToken);
        }
    }

    private static async Task<bool> ReadExactlyOrEofAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) return false;
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _acceptLoop; }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        Task[] connections;
        lock (_connections) connections = [.. _connections];
        try { await Task.WhenAll(connections); }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        _stop.Dispose();
    }
}
