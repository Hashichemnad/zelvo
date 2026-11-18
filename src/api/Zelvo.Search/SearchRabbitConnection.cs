using RabbitMQ.Client;

namespace Zelvo.Search;

/// <summary>One RabbitMQ connection for the worker's search jobs, opened on first use and reopened after it closes.</summary>
public sealed class SearchRabbitConnection(Uri address, string clientName) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IConnection? _connection;

    /// <summary>The open connection.</summary>
    public async Task<IConnection> GetAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } open)
        {
            return open;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is { IsOpen: true } again)
            {
                return again;
            }

            if (_connection is not null)
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }

            var factory = new ConnectionFactory { Uri = address, ClientProvidedName = clientName, AutomaticRecoveryEnabled = false };
            _connection = await factory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            return _connection;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }

        _gate.Dispose();
    }
}
