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
	internal Page(Browser browser, CdpConnection connection, string targetId, string sessionId, string? contextId = null)
	{
		Browser = browser;
		Connection = connection;
		TargetId = targetId;
		SessionId = sessionId;
		ContextId = contextId;
	}

	internal Browser Browser { get; }

	internal string? ContextId { get; }

	// Set once interaction recording is on (PagePerformance.RecordInteractionsAsync).
	internal int RecordingInteractions;

	private readonly PageLog _log = new();

	/// <summary>The page's console calls since it was created or last cleared, oldest first.</summary>
	public IReadOnlyList<ConsoleMessage> ConsoleMessages => _log.Console;

	/// <summary>The page's uncaught exceptions and unhandled rejections since it was created or last cleared.</summary>
	public IReadOnlyList<PageError> Errors => _log.Errors;

	/// <summary>
	/// Records the page's requests from now on, for a request list or a HAR file (<see cref="NetworkRecording"/>).
	/// Opt-in: nothing is collected until asked.
	/// </summary>
	public Task<NetworkRecording> RecordNetworkAsync(CancellationToken cancellationToken = default) =>
		NetworkRecording.StartAsync(this, cancellationToken);

	/// <summary>Forgets the console messages collected so far.</summary>
	public void ClearConsole() => _log.ClearConsole();

	/// <summary>Forgets the errors collected so far.</summary>
	public void ClearErrors() => _log.ClearErrors();

	// The main frame's lifecycle events for its current document (load, DOMContentLoaded, networkIdle, ...).
	private volatile ConcurrentDictionary<string, byte> _lifecycle = new(StringComparer.Ordinal);

	/// <summary>The connection, for commands this library does not wrap; pass <see cref="SessionId"/> with them.</summary>
	public CdpConnection Connection { get; }

	/// <summary>The protocol's <c>targetId</c>.</summary>
	public string TargetId { get; }

	/// <summary>The flattened session that commands for this page carry.</summary>
	public string SessionId { get; }

	/// <summary>How long a navigation may take before it fails. Defaults to 30 seconds.</summary>
	public TimeSpan NavigationTimeout { get; set; } = TimeSpan.FromSeconds(30);

	/// <summary>How long a locator waits for its element before an action or read fails. Defaults to 5 seconds.</summary>
	public TimeSpan ActionTimeout { get; set; } = TimeSpan.FromSeconds(5);

	/// <summary>The mouse.</summary>
	public Mouse Mouse => field ??= new Mouse(this);

	/// <summary>The keyboard; it types into whatever has focus.</summary>
	public Keyboard Keyboard => field ??= new Keyboard(this);

	/// <summary>Elements matching a CSS selector.</summary>
	public Locator Css(string selector) => Locate(LocatorKind.Css, selector);

	/// <summary>Elements with an ARIA role, and an accessible name when <paramref name="name"/> is given.</summary>
	public Locator Role(string role, string? name = null, bool exact = false) => Locate(LocatorKind.Role, role, name, exact);

	/// <summary>The innermost elements whose text contains <paramref name="text"/>.</summary>
	public Locator Text(string text, bool exact = false) => Locate(LocatorKind.Text, text, exact: exact);

	/// <summary>Controls named by a label, <c>aria-label</c> or <c>aria-labelledby</c>.</summary>
	public Locator Label(string label, bool exact = false) => Locate(LocatorKind.Label, label, exact: exact);

	/// <summary>Elements by <c>placeholder</c>.</summary>
	public Locator Placeholder(string text, bool exact = false) => Locate(LocatorKind.Placeholder, text, exact: exact);

	/// <summary>Elements by <c>alt</c> text.</summary>
	public Locator Alt(string text, bool exact = false) => Locate(LocatorKind.Alt, text, exact: exact);

	/// <summary>Elements by <c>title</c>.</summary>
	public Locator Title(string text, bool exact = false) => Locate(LocatorKind.Title, text, exact: exact);

	/// <summary>Elements by <c>data-testid</c>, matched exactly.</summary>
	public Locator TestId(string id) => Locate(LocatorKind.TestId, id);

	/// <summary>A locator of any kind.</summary>
	public Locator Locate(LocatorKind kind, string value, string? name = null, bool exact = false)
	{
		ArgumentException.ThrowIfNullOrEmpty(value);
		if (name is not null && kind != LocatorKind.Role)
			throw new ArgumentException("Only role locators take a name.", nameof(name));
		return new Locator(this, kind, value, name, exact);
	}

	/// <summary>
	/// Refuses every request of this page that <paramref name="allow"/> rejects, from now until the guard is disposed.
	/// </summary>
	public Task<NetworkGuard> GuardNetworkAsync(Func<Uri, bool> allow, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(allow);
		return NetworkGuard.StartAsync(this, allow, cancellationToken);
	}

	/// <summary>Presses a key or chord on whatever has focus, then waits for any navigation it starts.</summary>
	public Task PressAsync(string keys, CancellationToken cancellationToken = default) =>
		WithNavigationAsync(() => Keyboard.PressAsync(keys, cancellationToken), cancellationToken);

	/// <summary>Goes back one entry in the history; does nothing at the first entry.</summary>
	public Task BackAsync(CancellationToken cancellationToken = default) => HistoryAsync(-1, cancellationToken);

	/// <summary>Goes forward one entry in the history; does nothing at the last entry.</summary>
	public Task ForwardAsync(CancellationToken cancellationToken = default) => HistoryAsync(1, cancellationToken);

	/// <summary>Reloads the page and waits for it to load.</summary>
	public Task ReloadAsync(CancellationToken cancellationToken = default) =>
		WithNavigationAsync(() => SendAsync("Page.reload", null, cancellationToken), cancellationToken);

	private async Task HistoryAsync(int step, CancellationToken cancellationToken)
	{
		var history = await SendAsync("Page.getNavigationHistory", null, cancellationToken).ConfigureAwait(false);
		var index = history.GetProperty("currentIndex").GetInt32() + step;
		var entries = history.GetProperty("entries");
		if (index < 0 || index >= entries.GetArrayLength())
			return;
		var id = entries[index].GetProperty("id").GetInt32();
		await WithNavigationAsync(() => SendAsync("Page.navigateToHistoryEntry", w => w.WriteNumber("entryId", id), cancellationToken), cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>Sets the viewport size in CSS pixels, and the device pixel ratio.</summary>
	public async Task SetViewportAsync(int width, int height, double scale = 1, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
		await SendAsync("Emulation.setDeviceMetricsOverride", w =>
		{
			w.WriteNumber("width", width);
			w.WriteNumber("height", height);
			w.WriteNumber("deviceScaleFactor", scale);
			w.WriteBoolean("mobile", false);
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Emulates a device from <see cref="Devices"/>: viewport, pixel ratio, touch and user agent.</summary>
	public async Task SetDeviceAsync(string name, CancellationToken cancellationToken = default)
	{
		if (!Devices.All.TryGetValue(name, out var device))
			throw new ArgumentException($"Unknown device '{name}'. Known: {string.Join(", ", Devices.All.Keys.Order(StringComparer.Ordinal))}.", nameof(name));
		await SendAsync("Emulation.setDeviceMetricsOverride", w =>
		{
			w.WriteNumber("width", device.Width);
			w.WriteNumber("height", device.Height);
			w.WriteNumber("deviceScaleFactor", device.Scale);
			w.WriteBoolean("mobile", device.Mobile);
		}, cancellationToken).ConfigureAwait(false);
		await SendAsync("Emulation.setTouchEmulationEnabled", w => w.WriteBoolean("enabled", device.Mobile), cancellationToken).ConfigureAwait(false);
		if (device.UserAgent.Length > 0)
			await SendAsync("Emulation.setUserAgentOverride", w => w.WriteString("userAgent", device.UserAgent), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Reports this position to <c>navigator.geolocation</c>, and grants the page permission to read it.</summary>
	public async Task SetGeolocationAsync(double latitude, double longitude, CancellationToken cancellationToken = default)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(latitude, -90);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(latitude, 90);
		ArgumentOutOfRangeException.ThrowIfLessThan(longitude, -180);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(longitude, 180);
		await Connection.SendAsync("Browser.grantPermissions", w =>
		{
			w.WriteStartArray("permissions");
			w.WriteStringValue("geolocation");
			w.WriteEndArray();
			if (ContextId is not null)
				w.WriteString("browserContextId", ContextId);
		}, cancellationToken: cancellationToken).ConfigureAwait(false);
		await SendAsync("Emulation.setGeolocationOverride", w =>
		{
			w.WriteNumber("latitude", latitude);
			w.WriteNumber("longitude", longitude);
			w.WriteNumber("accuracy", 1);
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Takes the page off the network, or puts it back.</summary>
	public async Task SetOfflineAsync(bool offline, CancellationToken cancellationToken = default)
	{
		await SendAsync("Network.enable", null, cancellationToken).ConfigureAwait(false);
		await SendAsync("Network.emulateNetworkConditions", w =>
		{
			w.WriteBoolean("offline", offline);
			w.WriteNumber("latency", 0);
			w.WriteNumber("downloadThroughput", -1);
			w.WriteNumber("uploadThroughput", -1);
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Sends these headers with every request of the page; an empty set clears them.</summary>
	public async Task SetExtraHeadersAsync(IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(headers);
		await SendAsync("Network.enable", null, cancellationToken).ConfigureAwait(false);
		await SendAsync("Network.setExtraHTTPHeaders", w =>
		{
			w.WriteStartObject("headers");
			foreach (var (name, value) in headers)
				w.WriteString(name, value);
			w.WriteEndObject();
		}, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Scrolls the page with the mouse wheel at the centre of the viewport.</summary>
	public async Task ScrollAsync(double deltaX, double deltaY, CancellationToken cancellationToken = default)
	{
		var size = await EvaluateAsync("[innerWidth, innerHeight]", cancellationToken).ConfigureAwait(false);
		await Mouse.WheelAsync(size[0].GetDouble() / 2, size[1].GetDouble() / 2, deltaX, deltaY, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Waits until the current document reached <paramref name="state"/>: <c>load</c>, <c>domcontentloaded</c> or
	/// <c>networkidle</c> (no network activity for half a second), up to <see cref="NavigationTimeout"/>.
	/// </summary>
	public async Task WaitForLoadStateAsync(string state, CancellationToken cancellationToken = default)
	{
		var name = state switch
		{
			"load" => "load",
			"domcontentloaded" => "DOMContentLoaded",
			"networkidle" => "networkIdle",
			_ => throw new ArgumentException($"Unknown load state '{state}': use load, domcontentloaded or networkidle.", nameof(state)),
		};
		await PollAsync(() => Task.FromResult(_lifecycle.ContainsKey(name)), $"the page to reach '{state}'", cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Waits until the URL matches <paramref name="pattern"/>, a glob where <c>**</c> matches anything and <c>*</c>
	/// anything but <c>/</c>, up to <see cref="NavigationTimeout"/>.
	/// </summary>
	public async Task WaitForUrlAsync(string pattern, CancellationToken cancellationToken = default)
	{
		var glob = UrlGlob(pattern);
		await PollAsync(async () => glob.IsMatch(await UrlAsync(cancellationToken).ConfigureAwait(false)), $"the URL to match '{pattern}'", cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>A URL glob as agent-browser takes it: <c>**</c> matches anything, <c>*</c> anything but <c>/</c>, <c>?</c> one character.</summary>
	public static System.Text.RegularExpressions.Regex UrlGlob(string pattern)
	{
		var regex = new System.Text.StringBuilder("^");
		for (var i = 0; i < pattern.Length; i++)
		{
			if (pattern[i] == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
			{
				regex.Append(".*");
				i++;
			}
			else if (pattern[i] == '*')
				regex.Append("[^/]*");
			else if (pattern[i] == '?')
				regex.Append("[^/]");
			else
				regex.Append(System.Text.RegularExpressions.Regex.Escape(pattern[i].ToString()));
		}

		return new System.Text.RegularExpressions.Regex(regex.Append('$').ToString(), System.Text.RegularExpressions.RegexOptions.CultureInvariant);
	}

	private async Task PollAsync(Func<Task<bool>> done, string what, CancellationToken cancellationToken)
	{
		var started = System.Diagnostics.Stopwatch.StartNew();
		while (!await done().ConfigureAwait(false))
		{
			if (started.Elapsed >= NavigationTimeout)
				throw new TimeoutException($"Timed out after {NavigationTimeout.TotalSeconds:0}s waiting for {what}.");
			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Emulates <c>prefers-color-scheme</c>: <c>dark</c>, <c>light</c> or <c>no-preference</c>.</summary>
	public async Task SetColorSchemeAsync(string scheme, CancellationToken cancellationToken = default)
	{
		if (scheme is not ("dark" or "light" or "no-preference"))
			throw new ArgumentException($"Unknown color scheme '{scheme}': use dark, light or no-preference.", nameof(scheme));
		await SendAsync("Emulation.setEmulatedMedia", w =>
		{
			w.WriteStartArray("features");
			w.WriteStartObject();
			w.WriteString("name", "prefers-color-scheme");
			w.WriteString("value", scheme);
			w.WriteEndObject();
			w.WriteEndArray();
		}, cancellationToken).ConfigureAwait(false);
	}

	internal async Task InitializeAsync(CancellationToken cancellationToken)
	{
		// Lives as long as the page: events stop when its session ends.
		_ = Connection.On(SessionId, e =>
		{
			if (e.Method != "Page.lifecycleEvent" || e.Params.StringOrNull("frameId") != TargetId || e.Params.StringOrNull("name") is not { } name)
				return;
			// "init" starts a new document: its events replace the old one's.
			if (name == "init")
				_lifecycle = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
			_lifecycle.TryAdd(name, 0);
		});
		_ = Connection.On(SessionId, _log.OnEvent);
		await SendAsync("Page.enable", null, cancellationToken).ConfigureAwait(false);
		// Console calls and uncaught errors arrive as Runtime events.
		await SendAsync("Runtime.enable", null, cancellationToken).ConfigureAwait(false);
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
		using var watch = new NavigationWatch(this);
		try
		{
			var result = await SendAsync("Page.navigate", w => w.WriteString("url", url), waiting.Token).ConfigureAwait(false);
			if (result.StringOrNull("errorText") is { Length: > 0 } error)
				throw new PageNavigationException(url, error);
			if (result.StringOrNull("loaderId") is not { } loaderId || result.StringOrNull("frameId") is not { } frameId)
				return;
			await watch.LoadedAsync(frameId, loaderId, waiting.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException($"Navigating to {url} did not finish loading within {NavigationTimeout.TotalSeconds:0} seconds.");
		}
	}

	/// <summary>
	/// Runs <paramref name="action"/>; when it started a navigation of the page, waits for the new document to load
	/// (or for a navigation within the document to happen).
	/// </summary>
	internal async Task WithNavigationAsync(Func<Task> action, CancellationToken cancellationToken)
	{
		using var watch = new NavigationWatch(this);
		await action().ConfigureAwait(false);
		if (!watch.Started)
		{
			// Chrome can acknowledge the input before the navigation it caused is announced. A round trip to the page
			// runs after the renderer has handled the input, including asking to navigate; check again after it.
			await SendAsync("Runtime.evaluate", w => w.WriteString("expression", "0"), cancellationToken).ConfigureAwait(false);
			if (!watch.Started)
				return;
		}
		using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		waiting.CancelAfter(NavigationTimeout);
		try
		{
			await watch.SettledAsync(waiting.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new TimeoutException($"A navigation the action started did not finish loading within {NavigationTimeout.TotalSeconds:0} seconds.");
		}
	}

	/// <summary>
	/// Follows the main frame's navigation events from the moment it is created, since they can arrive before the
	/// command that caused them answers. The main frame's id is the target id.
	/// </summary>
	private sealed class NavigationWatch : IDisposable
	{
		private readonly Page _page;
		private readonly IDisposable _listening;
		private readonly ConcurrentDictionary<(string Frame, string Loader), TaskCompletionSource> _loads = new();
		private readonly TaskCompletionSource<string> _committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _started;

		public NavigationWatch(Page page)
		{
			_page = page;
			_listening = page.Connection.On(page.SessionId, OnEvent);
		}

		public bool Started => Volatile.Read(ref _started) == 1;

		private void OnEvent(Protocol.CdpEvent e)
		{
			var main = e.Params.StringOrNull("frameId") == _page.TargetId;
			switch (e.Method)
			{
				case "Page.lifecycleEvent" when e.Params.StringOrNull("name") == "load"
					&& e.Params.StringOrNull("frameId") is { } frame && e.Params.StringOrNull("loaderId") is { } loader:
					Signal(frame, loader).TrySetResult();
					break;
				case "Page.frameRequestedNavigation" or "Page.frameScheduledNavigation" or "Page.frameStartedNavigating" or "Page.frameStartedLoading" when main:
					Volatile.Write(ref _started, 1);
					break;
				case "Page.frameNavigated" when e.Params.TryGetProperty("frame", out var f) && f.StringOrNull("id") == _page.TargetId
					&& f.StringOrNull("loaderId") is { } committed:
					Volatile.Write(ref _started, 1);
					_committed.TrySetResult(committed);
					break;
				case "Page.navigatedWithinDocument" when main:
					Volatile.Write(ref _started, 1);
					_settled.TrySetResult();
					break;
				// Loading stopped without a new document: the navigation was cancelled or became a download.
				case "Page.frameStoppedLoading" when main && !_committed.Task.IsCompleted:
					_settled.TrySetResult();
					break;
			}
		}

		private TaskCompletionSource Signal(string frame, string loader) =>
			_loads.GetOrAdd((frame, loader), static _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

		/// <summary>Waits for the load of one loader, or fails when the connection closes.</summary>
		public async Task LoadedAsync(string frameId, string loaderId, CancellationToken cancellationToken) =>
			await (await Task.WhenAny(Signal(frameId, loaderId).Task, _page.Connection.Closed).WaitAsync(cancellationToken).ConfigureAwait(false))
				.ConfigureAwait(false);

		/// <summary>Waits until a started navigation committed and loaded, stayed within the document, or stopped.</summary>
		public async Task SettledAsync(CancellationToken cancellationToken)
		{
			var first = await Task.WhenAny(_committed.Task, _settled.Task, _page.Connection.Closed).WaitAsync(cancellationToken).ConfigureAwait(false);
			await first.ConfigureAwait(false);
			if (first == _committed.Task)
				await LoadedAsync(_page.TargetId, await _committed.Task.ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
		}

		public void Dispose() => _listening.Dispose();
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
