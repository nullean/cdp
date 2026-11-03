using System.Collections.Concurrent;
using System.Text.Json;
using Nullean.Cdp.Accessibility;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp;

/// <summary>A navigation failed: the browser reported an error, such as an unreachable host or a missing file.</summary>
public sealed class PageNavigationException(string url, string error)
	: Exception($"Navigating to {url} failed: {error}")
{
	/// <summary>The URL that was opened.</summary>
	public string Url { get; } = url;

	/// <summary>The browser's error, e.g. <c>net::ERR_FILE_NOT_FOUND</c>.</summary>
	public string Error { get; } = error;
}

/// <summary>Script on the page threw.</summary>
public sealed class PageScriptException(string message) : Exception(message);

/// <summary>A selector matched nothing.</summary>
public sealed class PageSelectorException(string selector)
	: Exception($"Selector '{selector}' did not match any element")
{
	/// <summary>The selector.</summary>
	public string Selector { get; } = selector;
}

/// <summary>One page (tab), attached through its own session on the browser's connection.</summary>
public sealed class Page
{
	internal Page(CdpConnection connection, string targetId, string sessionId)
	{
		Connection = connection;
		TargetId = targetId;
		SessionId = sessionId;
	}

	/// <summary>The connection, for commands this library does not wrap; pass <see cref="SessionId"/> with them.</summary>
	public CdpConnection Connection { get; }

	/// <summary>The protocol's <c>targetId</c>.</summary>
	public string TargetId { get; }

	/// <summary>The flattened session that commands for this page carry.</summary>
	public string SessionId { get; }

	/// <summary>How long a navigation may take before it fails. Defaults to 30 seconds.</summary>
	public TimeSpan NavigationTimeout { get; set; } = TimeSpan.FromSeconds(30);

	internal async Task InitializeAsync(CancellationToken cancellationToken)
	{
		await SendAsync("Page.enable", null, cancellationToken).ConfigureAwait(false);
		await SendAsync("Page.setLifecycleEventsEnabled", w => w.WriteBoolean("enabled", true), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Sends a command on this page's session.</summary>
	public Task<JsonElement> SendAsync(string method, Action<Utf8JsonWriter>? parameters = null, CancellationToken cancellationToken = default) =>
		Connection.SendAsync(method, parameters, SessionId, cancellationToken);

	/// <summary>
	/// Opens <paramref name="url"/> and waits for its <c>load</c> event. A navigation within the document (a fragment)
	/// has no load event and returns at once.
	/// </summary>
	/// <remarks>
	/// It waits for the <c>load</c> lifecycle event of this navigation's loader, not the next load event: a new page's
	/// own <c>about:blank</c> load can arrive late and would otherwise end the wait early. The event can also arrive
	/// before <c>Page.navigate</c> answers, so loads are recorded from before the command is sent.
	/// </remarks>
	public async Task GotoAsync(string url, CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrEmpty(url);
		using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		waiting.CancelAfter(NavigationTimeout);
		var loads = new ConcurrentDictionary<(string Frame, string Loader), TaskCompletionSource>();
		using var listening = Connection.On(SessionId, e =>
		{
			if (e.Method == "Page.lifecycleEvent" && e.Params.StringOrNull("name") == "load"
				&& e.Params.StringOrNull("frameId") is { } frame && e.Params.StringOrNull("loaderId") is { } loader)
				loads.GetOrAdd((frame, loader), NewSignal).TrySetResult();
		});
		try
		{
			var result = await SendAsync("Page.navigate", w => w.WriteString("url", url), waiting.Token).ConfigureAwait(false);
			if (result.StringOrNull("errorText") is { Length: > 0 } error)
				throw new PageNavigationException(url, error);
			if (result.StringOrNull("loaderId") is not { } loaderId || result.StringOrNull("frameId") is not { } frameId)
				return;
			// The load, or a closed connection, whichever comes first.
			var loaded = loads.GetOrAdd((frameId, loaderId), NewSignal).Task;
			await (await Task.WhenAny(loaded, Connection.Closed).WaitAsync(waiting.Token).ConfigureAwait(false)).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException($"Navigating to {url} did not finish loading within {NavigationTimeout.TotalSeconds:0} seconds.");
		}

		static TaskCompletionSource NewSignal(ValueTuple<string, string> _) => new(TaskCreationOptions.RunContinuationsAsynchronously);
	}

	/// <summary>Evaluates <paramref name="expression"/> and returns its value as JSON; promises are awaited.</summary>
	public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
	{
		var result = await SendAsync("Runtime.evaluate", w =>
		{
			w.WriteString("expression", expression);
			w.WriteBoolean("returnByValue", true);
			w.WriteBoolean("awaitPromise", true);
		}, cancellationToken).ConfigureAwait(false);
		if (result.TryGetProperty("exceptionDetails", out var details))
		{
			var text = details.TryGetProperty("exception", out var exception) && exception.StringOrNull("description") is { } description
				? description
				: details.StringOrNull("text") ?? "Script threw.";
			throw new PageScriptException(text);
		}

		return result.TryGetProperty("result", out var value) && value.TryGetProperty("value", out var v) ? v : default;
	}

	/// <summary>The page's current URL.</summary>
	public async Task<string> UrlAsync(CancellationToken cancellationToken = default) =>
		(await EvaluateAsync("location.href", cancellationToken).ConfigureAwait(false)).GetString() ?? string.Empty;

	/// <summary>The document's title.</summary>
	public async Task<string> TitleAsync(CancellationToken cancellationToken = default) =>
		(await EvaluateAsync("document.title", cancellationToken).ConfigureAwait(false)).GetString() ?? string.Empty;

	/// <summary>
	/// The accessibility tree, as agent-browser shows it before filtering: ignored and unnamed generic nodes are lifted
	/// into their parents. With <paramref name="selector"/>, the tree of the first element it matches.
	/// </summary>
	public async Task<IReadOnlyList<AxNode>> AccessibilityTreeAsync(string? selector = null, CancellationToken cancellationToken = default)
	{
		long? root = selector is null ? null : await BackendNodeIdAsync(selector, cancellationToken).ConfigureAwait(false);
		var tree = await SendAsync("Accessibility.getFullAXTree", null, cancellationToken).ConfigureAwait(false);
		return AxTree.Build(tree.GetProperty("nodes"), root);
	}

	/// <summary>The accessibility tree as text: <c>- role "name" [attributes]: value</c>, two spaces per level.</summary>
	public async Task<string> SnapshotAsync(AxSnapshotOptions? options = null, CancellationToken cancellationToken = default)
	{
		options ??= AxSnapshotOptions.Default;
		return AxPrinter.Print(await AccessibilityTreeAsync(options.Selector, cancellationToken).ConfigureAwait(false), options);
	}

	private async Task<long> BackendNodeIdAsync(string selector, CancellationToken cancellationToken)
	{
		var document = await SendAsync("DOM.getDocument", w => w.WriteNumber("depth", 0), cancellationToken).ConfigureAwait(false);
		var rootId = document.GetProperty("root").GetProperty("nodeId").GetInt32();
		var found = await SendAsync("DOM.querySelector", w =>
		{
			w.WriteNumber("nodeId", rootId);
			w.WriteString("selector", selector);
		}, cancellationToken).ConfigureAwait(false);
		var nodeId = found.GetProperty("nodeId").GetInt32();
		if (nodeId == 0)
			throw new PageSelectorException(selector);
		var described = await SendAsync("DOM.describeNode", w => w.WriteNumber("nodeId", nodeId), cancellationToken).ConfigureAwait(false);
		return described.GetProperty("node").GetProperty("backendNodeId").GetInt64();
	}
}
