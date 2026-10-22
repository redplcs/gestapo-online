using System.Net;

namespace Quazal.Prudp;

/// <summary>
/// Represents a PRUDP connection accepted by a <see cref="PrudpListener"/>.
/// </summary>
public sealed class PrudpConnection : IAsyncDisposable
{
    private PrudpConnection()
    {
    }

    /// <summary>
    /// Gets the remote endpoint of the connection.
    /// </summary>
    public IPEndPoint RemoteEndPoint => throw new NotImplementedException();

    /// <summary>
    /// Returns an <see cref="IAsyncEnumerable{T}"/> that enables reading all of the messages from the connection.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the enumeration.</param>
    /// <returns>The created async enumerable.</returns>
    /// <remarks>
    /// The enumeration completes when the connection is closed.
    /// </remarks>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAllAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>
    /// Queues a message to be sent reliably to the remote endpoint.
    /// </summary>
    /// <param name="message">The message to send.</param>
    public void Send(ReadOnlySpan<byte> message) => throw new NotImplementedException();

    /// <summary>
    /// Closes the connection and releases all resources associated with it.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
