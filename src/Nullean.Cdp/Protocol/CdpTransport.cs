using System.Buffers;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Nullean.Cdp.Protocol;

/// <summary>Moves whole CDP messages (UTF-8 JSON) to and from the browser.</summary>
internal interface ICdpTransport : IAsyncDisposable
{
	/// <summary>Sends one message. Safe to call from several threads at once.</summary>
	ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken);

	/// <summary>The next whole message, or <c>null</c> once the browser has closed the connection.</summary>
	ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// A <see cref="ClientWebSocket"/> transport. A web socket allows one send at a time, so sends go through a channel
/// drained by a single writer instead of a lock.
/// </summary>
internal sealed class WebSocketTransport : ICdpTransport
{
	private readonly ClientWebSocket _socket;
	private readonly Channel<(ReadOnlyMemory<byte> Message, TaskCompletionSource Sent)> _outgoing = Channel.CreateUnbounded<(ReadOnlyMemory<byte>, TaskCompletionSource)>(new UnboundedChannelOptions
	{
		SingleReader = true
	});
	private readonly CancellationTokenSource _closing = new();
	private readonly Task _writer;
	private readonly ArrayBufferWriter<byte> _incoming = new(64 * 1024);

	private WebSocketTransport(ClientWebSocket socket)
	{
		_socket = socket;
		_writer = Task.Run(WriteLoopAsync);
	}

	public static async Task<WebSocketTransport> ConnectAsync(Uri url, CancellationToken cancellationToken)
	{
		var socket = new ClientWebSocket();
		// A full accessibility tree can be many megabytes; the default 16 KiB receive buffer only means more reads.
		socket.Options.SetBuffer(64 * 1024, 64 * 1024);
		try
		{
			await socket.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
		}
		catch
		{
			socket.Dispose();
			throw;
		}

		return new WebSocketTransport(socket);
	}

	public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
	{
		var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		if (!_outgoing.Writer.TryWrite((message, sent)))
			throw new CdpDisconnectedException("The connection to the browser is closed.");
		return new ValueTask(sent.Task.WaitAsync(cancellationToken));
	}

	private async Task WriteLoopAsync()
	{
		await foreach (var (message, sent) in _outgoing.Reader.ReadAllAsync().ConfigureAwait(false))
		{
			try
			{
				await _socket.SendAsync(message, WebSocketMessageType.Text, endOfMessage: true, _closing.Token).ConfigureAwait(false);
				sent.TrySetResult();
			}
			catch (Exception e)
			{
				sent.TrySetException(new CdpDisconnectedException("Sending to the browser failed: " + e.Message, e));
			}
		}
	}

	// Only the connection's receive loop calls this, so the shared buffer is never used by two readers.
	public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
	{
		_incoming.ResetWrittenCount();
		while (true)
		{
			ValueWebSocketReceiveResult result;
			try
			{
				result = await _socket.ReceiveAsync(_incoming.GetMemory(16 * 1024), cancellationToken).ConfigureAwait(false);
			}
			catch (WebSocketException)
			{
				return null;
			}

			if (result.MessageType == WebSocketMessageType.Close)
				return null;
			_incoming.Advance(result.Count);
			if (result.EndOfMessage)
				return _incoming.WrittenMemory.ToArray();
		}
	}

	public async ValueTask DisposeAsync()
	{
		_outgoing.Writer.TryComplete();
		await _closing.CancelAsync().ConfigureAwait(false);
		try
		{
			await _writer.ConfigureAwait(false);
		}
		catch (OperationCanceledException) { }

		if (_socket.State == WebSocketState.Open)
		{
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
			}
			catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
		}

		_socket.Dispose();
		_closing.Dispose();
	}
}
