using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AwesomeAssertions;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp.Tests;

public class CdpConnectionTests
{
	/// <summary>The browser's side of an in-memory transport: read what was sent, push replies and events.</summary>
	private sealed class FakeBrowser : ICdpTransport
	{
		private readonly Channel<ReadOnlyMemory<byte>?> _toClient = Channel.CreateUnbounded<ReadOnlyMemory<byte>?>();
		private readonly Channel<JsonElement> _fromClient = Channel.CreateUnbounded<JsonElement>();

		public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken)
		{
			using var document = JsonDocument.Parse(message);
			_fromClient.Writer.TryWrite(document.RootElement.Clone());
			return ValueTask.CompletedTask;
		}

		public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken) => await _toClient.Reader.ReadAsync(cancellationToken);

		public async Task<JsonElement> NextRequestAsync() => await _fromClient.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

		public void Push(string json) => _toClient.Writer.TryWrite(Encoding.UTF8.GetBytes(json));

		public void Close() => _toClient.Writer.TryWrite(null);

		public ValueTask DisposeAsync()
		{
			_toClient.Writer.TryComplete();
			return ValueTask.CompletedTask;
		}
	}

	private static TimeSpan Wait => TimeSpan.FromSeconds(5);

	[Test]
	public async Task WritesTheCommandWithIdSessionAndParams()
	{
		var browser = new FakeBrowser();
		await using var connection = new CdpConnection(browser);

		_ = connection.SendAsync("Page.navigate", w => w.WriteString("url", "about:blank"), "S1");
		var request = await browser.NextRequestAsync();

		request.GetProperty("id").GetInt32().Should().BePositive();
		request.GetProperty("method").GetString().Should().Be("Page.navigate");
		request.GetProperty("sessionId").GetString().Should().Be("S1");
		request.GetProperty("params").GetProperty("url").GetString().Should().Be("about:blank");
	}

	[Test]
	public async Task BrowserCommandsHaveNoSessionAndAnEmptyParamsObject()
	{
		var browser = new FakeBrowser();
		await using var connection = new CdpConnection(browser);

		_ = connection.SendAsync("Browser.getVersion");
		var request = await browser.NextRequestAsync();

		request.TryGetProperty("sessionId", out _).Should().BeFalse();
		request.GetProperty("params").EnumerateObject().Should().BeEmpty();
	}

	[Test]
	public async Task RepliesAreRoutedByIdInAnyOrder()
	{
		var browser = new FakeBrowser();
		await using var connection = new CdpConnection(browser);

		var first = connection.SendAsync("A");
		var second = connection.SendAsync("B");
		var a = (await browser.NextRequestAsync()).GetProperty("id").GetInt32();
		var b = (await browser.NextRequestAsync()).GetProperty("id").GetInt32();
		browser.Push($$$"""{"id":{{{b}}},"result":{"who":"B"}}""");
		browser.Push($$$"""{"id":{{{a}}},"result":{"who":"A"}}""");

		(await second.WaitAsync(Wait)).GetProperty("who").GetString().Should().Be("B");
		(await first.WaitAsync(Wait)).GetProperty("who").GetString().Should().Be("A");
	}

	[Test]
	public async Task ManyConcurrentCommandsEachGetTheirOwnReply()
	{
		var browser = new FakeBrowser();
		await using var connection = new CdpConnection(browser);

		var sends = Enumerable.Range(0, 200).Select(i => connection.SendAsync("Echo", w => w.WriteNumber("n", i))).ToArray();
		var requests = new List<JsonElement>();
		for (var i = 0; i < sends.Length; i++)
			requests.Add(await browser.NextRequestAsync());
		foreach (var request in Enumerable.Reverse(requests))
		{
			var n = request.GetProperty("params").GetProperty("n").GetInt32();
			browser.Push($$$"""{"id":{{{request.GetProperty("id").GetInt32()}}},"result":{"n":{{{n}}}}}""");
		}

		var results = await Task.WhenAll(sends).WaitAsync(Wait);
		results.Select(r => r.GetProperty("n").GetInt32()).Should().Equal(Enumerable.Range(0, 200));
	}

	[Test]
	public async Task AnErrorReplyThrowsWithTheMethodCodeAndMessage()
	{
		var browser = new FakeBrowser();
		await using var connection = new CdpConnection(browser);

		var send = connection.SendAsync("DOM.querySelector");
		var id = (await browser.NextRequestAsync()).GetProperty("id").GetInt32();
		browser.Push($$$"""{"id":{{{id}}},"error":{"code":-32000,"message":"DOM Error while querying","data":"bad selector"}}""");

		var act = () => send.WaitAsync(Wait);
		(await act.Should().ThrowAsync<CdpException>()).Which.Message.Should().Be("DOM.querySelector failed: DOM Error while querying: bad selector (-32000)");
	}

	[Test]
	public async Task EventsReachListenersOfTheirSessionOnly()
	{
		var browser = new FakeBrowser();
		await using var connection = new CdpConnection(browser);
		var seen = Channel.CreateUnbounded<string>();
		using var s1 = connection.On("S1", e => seen.Writer.TryWrite("S1:" + e.Method));
		using var none = connection.On(null, e => seen.Writer.TryWrite("browser:" + e.Method));

		browser.Push("""{"method":"Page.loadEventFired","sessionId":"S2","params":{}}""");
		browser.Push("""{"method":"Target.targetCreated","params":{}}""");
		browser.Push("""{"method":"Page.loadEventFired","sessionId":"S1","params":{"timestamp":1}}""");

		(await seen.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Should().Be("browser:Target.targetCreated");
		(await seen.Reader.ReadAsync().AsTask().WaitAsync(Wait)).Should().Be("S1:Page.loadEventFired");
	}

	[Test]
	public async Task WaitForEventMatchesMethodAndPredicate()
	{
		var browser = new FakeBrowser();
		await using var connection = new CdpConnection(browser);

		var wait = connection.WaitForEventAsync("S1", "Page.lifecycleEvent", p => p.GetProperty("name").GetString() == "load");
		browser.Push("""{"method":"Page.lifecycleEvent","sessionId":"S1","params":{"name":"init"}}""");
		browser.Push("""{"method":"Page.frameNavigated","sessionId":"S1","params":{"name":"load"}}""");
		browser.Push("""{"method":"Page.lifecycleEvent","sessionId":"S1","params":{"name":"load"}}""");

		var e = await wait.WaitAsync(Wait);
		e.Params.GetProperty("name").GetString().Should().Be("load");
		e.Method.Should().Be("Page.lifecycleEvent");
	}

	[Test]
	public async Task ClosingFailsPendingCommandsAndEventWaitsAndLaterSends()
	{
		var browser = new FakeBrowser();
		var connection = new CdpConnection(browser);

		var pending = connection.SendAsync("Never.answered");
		await browser.NextRequestAsync();
		var waiting = connection.WaitForEventAsync("S1", "Page.loadEventFired");
		browser.Close();

		await FluentActions
			.Awaiting(() => pending.WaitAsync(Wait))
			.Should()
			.ThrowAsync<CdpDisconnectedException>()
			.WithMessage("The browser closed the connection.");
		await FluentActions.Awaiting(() => waiting.WaitAsync(Wait)).Should().ThrowAsync<CdpDisconnectedException>();
		connection.IsClosed.Should().BeTrue();
		await FluentActions.Awaiting(() => connection.SendAsync("After.close")).Should().ThrowAsync<CdpDisconnectedException>();
		await FluentActions.Awaiting(() => connection.WaitForEventAsync(null, "X").WaitAsync(Wait)).Should().ThrowAsync<CdpDisconnectedException>();
		await connection.DisposeAsync();
	}

	[Test]
	public async Task DisposingFailsPendingCommands()
	{
		var browser = new FakeBrowser();
		var connection = new CdpConnection(browser);
		var pending = connection.SendAsync("Never.answered");
		await browser.NextRequestAsync();

		await connection.DisposeAsync();

		await FluentActions
			.Awaiting(() => pending.WaitAsync(Wait))
			.Should()
			.ThrowAsync<CdpDisconnectedException>()
			.WithMessage("The connection to the browser was disposed.");
	}

	// Which exception a pending read ends with when the connection is disposed depends on a race between the cancellation and
	// the transport closing underneath it (a channel completing, a socket disposed). Whatever it is, disposal started it.
	[Test]
	[Arguments("ObjectDisposedException")]
	[Arguments("ChannelClosedException")]
	[Arguments("WebSocketException")]
	public async Task AnyReadFailureAfterDisposalStartedIsReportedAsDisposed(string failure)
	{
		Exception exception = failure switch
		{
			"ObjectDisposedException" => new ObjectDisposedException("socket"),
			"ChannelClosedException" => new ChannelClosedException(),
			_ => new System.Net.WebSockets.WebSocketException(System.Net.WebSockets.WebSocketError.ConnectionClosedPrematurely),
		};
		var transport = new FailsWhenDisposed(exception);
		var connection = new CdpConnection(transport);
		var pending = connection.SendAsync("Never.answered");
		await transport.Reading.Task.WaitAsync(Wait);

		await connection.DisposeAsync();

		await FluentActions
			.Awaiting(() => pending.WaitAsync(Wait))
			.Should()
			.ThrowAsync<CdpDisconnectedException>()
			.WithMessage("The connection to the browser was disposed.");
	}

	[Test]
	public async Task AReadFailureWithoutDisposalIsReportedAsAReadFailure()
	{
		var transport = new FailsWhenDisposed(new InvalidOperationException("the socket broke"));
		await using var connection = new CdpConnection(transport);
		var pending = connection.SendAsync("Never.answered");
		await transport.Reading.Task.WaitAsync(Wait);

		transport.Fail();

		await FluentActions
			.Awaiting(() => pending.WaitAsync(Wait))
			.Should()
			.ThrowAsync<CdpDisconnectedException>()
			.WithMessage("Reading from the browser failed: the socket broke");
	}

	// A transport whose pending read fails with the given exception, not a cancellation, when it is disposed (or Fail is called).
	private sealed class FailsWhenDisposed(Exception failure) : ICdpTransport
	{
		private readonly TaskCompletionSource<ReadOnlyMemory<byte>?> _read = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken) => ValueTask.CompletedTask;

		public async ValueTask<ReadOnlyMemory<byte>?> ReceiveAsync(CancellationToken cancellationToken)
		{
			Reading.TrySetResult();
			return await _read.Task;
		}

		public void Fail() => _read.TrySetException(failure);

		public ValueTask DisposeAsync()
		{
			Fail();
			return ValueTask.CompletedTask;
		}
	}

	[Test]
	public async Task CancellingStopsWaitingAndForgetsTheCommand()
	{
		var browser = new FakeBrowser();
		await using var connection = new CdpConnection(browser);
		using var cancel = new CancellationTokenSource();

		var send = connection.SendAsync("Slow", cancellationToken: cancel.Token);
		var id = (await browser.NextRequestAsync()).GetProperty("id").GetInt32();
		await cancel.CancelAsync();
		await FluentActions.Awaiting(() => send).Should().ThrowAsync<OperationCanceledException>();

		// A late reply for the forgotten id is ignored, and the connection keeps working.
		browser.Push($$$"""{"id":{{{id}}},"result":{}}""");
		var next = connection.SendAsync("Next");
		var nextId = (await browser.NextRequestAsync()).GetProperty("id").GetInt32();
		browser.Push($$$"""{"id":{{{nextId}}},"result":{"ok":true}}""");
		(await next.WaitAsync(Wait)).GetProperty("ok").GetBoolean().Should().BeTrue();
	}
}
