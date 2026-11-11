using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Nullean.Cdp.Protocol;

/// <summary>An event the browser sent: its method, the session it belongs to (null for the browser), and its params.</summary>
public sealed record CdpEvent(string Method, string? SessionId, JsonElement Params);

/// <summary>The browser answered a command with an error.</summary>
public sealed class CdpException(string method, int code, string message) : Exception($"{method} failed: {message} ({code})")
{
	/// <summary>The command that failed.</summary>
	public string Method { get; } = method;

	/// <summary>The protocol's error code.</summary>
	public int Code { get; } = code;

	/// <summary>The protocol's error message.</summary>
	public string ProtocolMessage { get; } = message;
}

/// <summary>The connection to the browser closed, so the command can never be answered.</summary>
public sealed class CdpDisconnectedException : Exception
{
	/// <summary>Creates the exception.</summary>
	public CdpDisconnectedException(string message) : base(message)
	{ }

	/// <summary>Creates the exception with its cause.</summary>
	public CdpDisconnectedException(string message, Exception inner) : base(message, inner)
	{ }
}

/// <summary>
/// One connection to a browser, with flattened sessions: every page's commands and events travel over it, tagged with
/// a session id. Commands are matched to replies by id, without locks.
/// </summary>
public sealed class CdpConnection : IAsyncDisposable
{
	private readonly ICdpTransport _transport;
	private readonly ConcurrentDictionary<int, (string Method, TaskCompletionSource<JsonElement> Reply)> _pending = new();
	private readonly ConcurrentDictionary<long, (string? SessionId, Action<CdpEvent> Handler)> _listeners = new();
	// Event waits fail when the connection closes, as pending commands do.
	private readonly ConcurrentDictionary<TaskCompletionSource<CdpEvent>, byte> _closedWaiters = new();
	private readonly CancellationTokenSource _stopping = new();
	private readonly TaskCompletionSource _closedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly Task _receiver;
	private int _nextId;
	private long _nextListener;
	private CdpDisconnectedException? _closed;

	internal CdpConnection(ICdpTransport transport)
	{
		_transport = transport;
		_receiver = Task.Run(ReceiveLoopAsync);
	}

	/// <summary>Connects to a browser's DevTools web socket (<c>ws://127.0.0.1:port/devtools/browser/…</c>).</summary>
	public static async Task<CdpConnection> ConnectAsync(Uri webSocketUrl, CancellationToken cancellationToken = default) =>
		new(await WebSocketTransport.ConnectAsync(webSocketUrl, cancellationToken).ConfigureAwait(false));

	/// <summary>Whether the browser has closed the connection.</summary>
	public bool IsClosed => Volatile.Read(ref _closed) is not null;

	/// <summary>Faults with <see cref="CdpDisconnectedException"/> once the connection closes; never completes otherwise.</summary>
	internal Task Closed => _closedSignal.Task;

	/// <summary>
	/// Sends a command and returns its result. The result is detached from the message, so it stays valid.
	/// </summary>
	/// <param name="method">The command, e.g. <c>Page.navigate</c>.</param>
	/// <param name="parameters">Writes the params object's properties, or <c>null</c> for none.</param>
	/// <param name="sessionId">The page session, or <c>null</c> for the browser.</param>
	/// <param name="cancellationToken">Stops waiting for the reply. The command may still run.</param>
	public async Task<JsonElement> SendAsync(
		string method,
		Action<Utf8JsonWriter>? parameters = null,
		string? sessionId = null,
		CancellationToken cancellationToken = default
	)
	{
		ArgumentException.ThrowIfNullOrEmpty(method);
		if (Volatile.Read(ref _closed) is { } closed)
			throw closed;

		var id = Interlocked.Increment(ref _nextId);
		var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = (method, reply);
		// The receive loop may have closed between the check above and the add; fail rather than wait forever.
		if (Volatile.Read(ref _closed) is { } closedNow && _pending.TryRemove(id, out _))
			throw closedNow;

		try
		{
			await _transport.SendAsync(Write(id, method, parameters, sessionId), cancellationToken).ConfigureAwait(false);
			return await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			_pending.TryRemove(id, out _);
		}
	}

	private static ReadOnlyMemory<byte> Write(int id, string method, Action<Utf8JsonWriter>? parameters, string? sessionId)
	{
		var buffer = new ArrayBufferWriter<byte>(256);
		using (var json = new Utf8JsonWriter(buffer))
		{
			json.WriteStartObject();
			json.WriteNumber("id", id);
			json.WriteString("method", method);
			if (sessionId is not null)
				json.WriteString("sessionId", sessionId);
			json.WriteStartObject("params");
			parameters?.Invoke(json);
			json.WriteEndObject();
			json.WriteEndObject();
		}

		return buffer.WrittenMemory;
	}

	/// <summary>
	/// Calls <paramref name="handler"/> for every event of <paramref name="sessionId"/> until the returned handle is
	/// disposed. Handlers run on the receive loop, so they must be quick and must not block.
	/// </summary>
	public IDisposable On(string? sessionId, Action<CdpEvent> handler)
	{
		ArgumentNullException.ThrowIfNull(handler);
		var id = Interlocked.Increment(ref _nextListener);
		_listeners[id] = (sessionId, handler);
		return new Subscription(this, id);
	}

	/// <summary>
	/// The next event called <paramref name="method"/> on <paramref name="sessionId"/> that
	/// <paramref name="predicate"/> accepts. It listens from the moment it is called, so call it before the command
	/// that causes the event, then await it.
	/// </summary>
	public Task<CdpEvent> WaitForEventAsync(
		string? sessionId,
		string method,
		Func<JsonElement, bool>? predicate = null,
		CancellationToken cancellationToken = default
	)
	{
		var seen = new TaskCompletionSource<CdpEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
		var subscription = On(sessionId, e =>
		{
			if (e.Method == method && (predicate is null || predicate(e.Params)))
				seen.TrySetResult(e);
		});
		// Register first, then check: the receive loop sets _closed before it fails the registered waiters, so a close
		// that races with this call is seen by one side or the other.
		_closedWaiters[seen] = 0;
		if (Volatile.Read(ref _closed) is { } closed)
			seen.TrySetException(closed);

		return Finish();

		async Task<CdpEvent> Finish()
		{
			try
			{
				return await seen.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
			}
			finally
			{
				subscription.Dispose();
				_closedWaiters.TryRemove(seen, out _);
			}
		}
	}

	private async Task ReceiveLoopAsync()
	{
		var reason = "The browser closed the connection.";
		try
		{
			while (await _transport.ReceiveAsync(_stopping.Token).ConfigureAwait(false) is { } message)
				Dispatch(message);
		}
		catch (Exception) when (_stopping.IsCancellationRequested)
		{
			// DisposeAsync cancels before it disposes the transport, so once that has happened a failed read is the disposal
			// itself, whichever way the transport reports it: the cancellation, or the channel or socket closing under it.
			reason = "The connection to the browser was disposed.";
		}
		catch (Exception e)
		{
			reason = "Reading from the browser failed: " + e.Message;
		}

		var closed = new CdpDisconnectedException(reason);
		Volatile.Write(ref _closed, closed);
		foreach (var id in _pending.Keys)
		{
			if (_pending.TryRemove(id, out var pending))
				pending.Reply.TrySetException(closed);
		}

		foreach (var waiter in _closedWaiters.Keys)
			waiter.TrySetException(closed);

		_closedSignal.TrySetException(closed);
		// Mark the fault observed: nothing has to await Closed.
		_ = _closedSignal.Task.Exception;
	}

	private void Dispatch(ReadOnlyMemory<byte> message)
	{
		using var document = JsonDocument.Parse(message);
		var root = document.RootElement;
		var sessionId = root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;

		if (root.TryGetProperty("id", out var idElement) && idElement.TryGetInt32(out var id))
		{
			if (!_pending.TryGetValue(id, out var pending))
				return;
			if (root.TryGetProperty("error", out var error))
			{
				var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
				var text = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
				if (error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
					text += ": " + data.GetString();
				pending.Reply.TrySetException(new CdpException(pending.Method, code, text));
			}
			else
				pending.Reply.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
			return;
		}

		if (!root.TryGetProperty("method", out var methodElement) || methodElement.GetString() is not { } method)
			return;
		var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
		var e = new CdpEvent(method, sessionId, parameters);
		foreach (var (_, listener) in _listeners)
		{
			if (listener.SessionId == sessionId)
				listener.Handler(e);
		}
	}

	/// <summary>Closes the connection; pending commands fail with <see cref="CdpDisconnectedException"/>.</summary>
	public async ValueTask DisposeAsync()
	{
		await _stopping.CancelAsync().ConfigureAwait(false);
		await _transport.DisposeAsync().ConfigureAwait(false);
		await _receiver.ConfigureAwait(false);
		_stopping.Dispose();
	}

	private sealed class Subscription(CdpConnection owner, long id) : IDisposable
	{
		public void Dispose() => owner._listeners.TryRemove(id, out _);
	}
}
