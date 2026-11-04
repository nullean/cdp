using System.Collections.Concurrent;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp;

/// <summary>A request the guard refused.</summary>
/// <param name="Url">The URL.</param>
/// <param name="ResourceType">The protocol's resource type, e.g. <c>Document</c>, <c>Image</c>, <c>Script</c>.</param>
/// <param name="TopLevel">Whether it was a request of a page's top frame (the page or a popup), not of a frame in it.</param>
public sealed record BlockedRequest(string Url, string ResourceType, bool TopLevel)
{
	/// <summary>Whether the request was a page loading a new document: the page or a popup navigating.</summary>
	public bool IsNavigation => TopLevel && ResourceType == "Document";
}

/// <summary>
/// Holds every request of one page until a predicate allows it, through the protocol's <c>Fetch</c> domain:
/// navigations, frames and sub-resources, <c>file://</c> included. Popups (attached paused by the browser) and frames
/// that run in their own process (attached paused by the page) are guarded the same way before they run. <c>data:</c>, <c>blob:</c>
/// and <c>about:</c> URLs are not network requests and pass. Refused requests fail as blocked by the client and are
/// kept for <see cref="TakeBlocked"/>.
/// </summary>
public sealed class NetworkGuard : IAsyncDisposable
{
	private readonly Page _page;
	private readonly Func<Uri, bool> _allow;
	private readonly ConcurrentQueue<BlockedRequest> _blocked = new();
	// Every guarded session and the target (top frame) it belongs to; its listener, to stop on dispose.
	private readonly ConcurrentDictionary<string, (string TargetId, IDisposable Listening)> _sessions = new(StringComparer.Ordinal);
	private int _disposed;

	private NetworkGuard(Page page, Func<Uri, bool> allow)
	{
		_page = page;
		_allow = allow;
	}

	internal static async Task<NetworkGuard> StartAsync(Page page, Func<Uri, bool> allow, CancellationToken cancellationToken)
	{
		var guard = new NetworkGuard(page, allow);
		guard.Watch(page.SessionId, page.TargetId);
		page.Browser.OnPopup(page.TargetId, guard.GuardPopupAsync);
		await GuardSessionAsync(page.Connection, page.SessionId, cancellationToken).ConfigureAwait(false);
		return guard;
	}

	/// <summary>The requests refused since the last call, in order.</summary>
	public IReadOnlyList<BlockedRequest> TakeBlocked()
	{
		var taken = new List<BlockedRequest>();
		while (_blocked.TryDequeue(out var request))
			taken.Add(request);
		return taken;
	}

	// A popup is guarded before it runs, and so are the popups it opens.
	private async Task GuardPopupAsync(string sessionId, string targetId)
	{
		if (Volatile.Read(ref _disposed) == 1)
			return;
		Watch(sessionId, targetId);
		_page.Browser.OnPopup(targetId, GuardPopupAsync);
		await GuardSessionAsync(_page.Connection, sessionId, CancellationToken.None).ConfigureAwait(false);
	}

	private void Watch(string sessionId, string targetId) =>
		_sessions.TryAdd(sessionId, (targetId, _page.Connection.On(sessionId, e => OnEvent(sessionId, targetId, e))));

	// Fetch first, so nothing loads unguarded; then attach to whatever this target opens; then let it run.
	private static async Task GuardSessionAsync(CdpConnection connection, string sessionId, CancellationToken cancellationToken)
	{
		await connection.SendAsync("Fetch.enable", w =>
		{
			w.WriteStartArray("patterns");
			w.WriteStartObject();
			w.WriteString("urlPattern", "*");
			w.WriteString("requestStage", "Request");
			w.WriteEndObject();
			w.WriteEndArray();
		}, sessionId, cancellationToken).ConfigureAwait(false);
		await connection.SendAsync("Target.setAutoAttach", w =>
		{
			w.WriteBoolean("autoAttach", true);
			w.WriteBoolean("waitForDebuggerOnStart", true);
			w.WriteBoolean("flatten", true);
		}, sessionId, cancellationToken).ConfigureAwait(false);
	}

	private void OnEvent(string sessionId, string targetId, CdpEvent e)
	{
		switch (e.Method)
		{
			case "Fetch.requestPaused" when e.Params.StringOrNull("requestId") is { } id:
				Decide(sessionId, targetId, id, e);
				break;
			case "Target.attachedToTarget" when e.Params.StringOrNull("sessionId") is { } child
				&& e.Params.TryGetProperty("targetInfo", out var info) && info.StringOrNull("targetId") is { } childTarget:
				Watch(child, childTarget);
				Observe(AttachAsync(child, info.StringOrNull("type")));
				break;
		}
	}

	private async Task AttachAsync(string sessionId, string? type)
	{
		// Workers and the like may not support Fetch or auto-attach; they still have to be resumed.
		if (type is "page" or "iframe" or "other")
			await GuardSessionAsync(_page.Connection, sessionId, CancellationToken.None).ConfigureAwait(false);
		await _page.Connection.SendAsync("Runtime.runIfWaitingForDebugger", null, sessionId).ConfigureAwait(false);
	}

	private void Decide(string sessionId, string targetId, string requestId, CdpEvent e)
	{
		var url = e.Params.TryGetProperty("request", out var request) ? request.StringOrNull("url") ?? string.Empty : string.Empty;
		var allowed = Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme is "data" or "blob" or "about" || _allow(uri));
		if (!allowed)
			_blocked.Enqueue(new BlockedRequest(url, e.Params.StringOrNull("resourceType") ?? string.Empty, e.Params.StringOrNull("frameId") == targetId));

		// The handler runs on the receive loop and must not wait for the reply; failures only mean the target is gone.
		Observe(allowed
			? _page.Connection.SendAsync("Fetch.continueRequest", w => w.WriteString("requestId", requestId), sessionId)
			: _page.Connection.SendAsync("Fetch.failRequest", w =>
			{
				w.WriteString("requestId", requestId);
				w.WriteString("errorReason", "BlockedByClient");
			}, sessionId));
	}

	private static void Observe(Task task) =>
		_ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

	/// <summary>Stops guarding the page: its requests flow freely again.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 1)
			return;
		foreach (var (_, session) in _sessions)
		{
			session.Listening.Dispose();
			_page.Browser.OffPopup(session.TargetId);
		}
		if (_page.Connection.IsClosed)
			return;
		try
		{
			await _page.SendAsync("Fetch.disable").ConfigureAwait(false);
			await _page.SendAsync("Target.setAutoAttach", w =>
			{
				w.WriteBoolean("autoAttach", false);
				w.WriteBoolean("waitForDebuggerOnStart", false);
			}).ConfigureAwait(false);
		}
		catch (Exception e) when (e is CdpException or CdpDisconnectedException)
		{
		}
	}
}
