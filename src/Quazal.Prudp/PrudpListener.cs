using System.Net;

namespace Quazal.Prudp;

/// <summary>
/// Listens for incoming PRUDP connections.
/// </summary>
public sealed class PrudpListener : IAsyncDisposable
{
    private PrudpListener()
    {
    }

    /// <summary>
    /// Creates a new <see cref="PrudpListener"/> and starts listening for incoming connections.
    /// </summary>
    /// <param name="options">The options for the listener.</param>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The value of its <see cref="ValueTask{TResult}.Result"/>
    /// property contains the started listener.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="options"/> contains an invalid value.</exception>
    /// <exception cref="System.Net.Sockets.SocketException">An error occurred when binding the socket.</exception>
    public static ValueTask<PrudpListener> ListenAsync(PrudpListenerOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>
    /// Gets the local endpoint the listener is bound to.
    /// </summary>
    public IPEndPoint LocalEndPoint => throw new NotImplementedException();

    /// <summary>
    /// Accepts an incoming connection.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token that can be used to cancel the asynchronous operation.</param>
    /// <returns>
    /// A task that represents the asynchronous operation. The value of its <see cref="ValueTask{TResult}.Result"/>
    /// property contains the accepted connection.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The listener has been disposed.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was canceled.</exception>
    /// <remarks>
    /// Exceptions thrown by <see cref="PrudpListenerOptions.ConnectCallback"/> are propagated by this method.
    /// </remarks>
    public ValueTask<PrudpConnection> AcceptConnectionAsync(CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    /// <summary>
    /// Stops listening and closes all connections.
    /// </summary>
    /// <returns>A task that represents the asynchronous dispose operation.</returns>
    public ValueTask DisposeAsync() => throw new NotImplementedException();
}
