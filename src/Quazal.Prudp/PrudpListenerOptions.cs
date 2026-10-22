using System.Net;

namespace Quazal.Prudp;

/// <summary>
/// Represents the options for a <see cref="PrudpListener"/>.
/// </summary>
public sealed class PrudpListenerOptions
{
    /// <summary>
    /// Gets or sets the endpoint to listen on.
    /// </summary>
    public required IPEndPoint ListenEndPoint { get; set; }

    /// <summary>
    /// Gets or sets the access key used to compute packet checksums.
    /// </summary>
    public required string AccessKey { get; set; }

    /// <summary>
    /// Gets or sets the callback that validates incoming connection requests.
    /// </summary>
    /// <remarks>
    /// The callback receives the connection and the request payload, and returns the response payload,
    /// or <see langword="null"/> to reject the connection.
    /// </remarks>
    public Func<PrudpConnection, ReadOnlySpan<byte>, byte[]?>? ConnectCallback { get; set; }

    /// <summary>
    /// Gets or sets the time provider used by the listener.
    /// </summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}
