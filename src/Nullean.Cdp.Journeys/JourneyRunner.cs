using Nullean.Cdp.Accessibility;
using System.Globalization;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp.Journeys;

/// <summary>A viewport size, and its device scale factor.</summary>
/// <param name="Width">CSS pixels.</param>
/// <param name="Height">CSS pixels.</param>
/// <param name="Scale">The device scale factor.</param>
public sealed record JourneyViewport(int Width, int Height, double Scale = 1);

/// <summary>How a journey runs.</summary>
public sealed class JourneyOptions
{
	/// <summary>What relative <c>open</c> URLs resolve against.</summary>
	public Uri? BaseUrl { get; init; }

	/// <summary>Overrides <see cref="Page.ActionTimeout"/> for the run.</summary>
	public TimeSpan? ActionTimeout { get; init; }

	/// <summary>Overrides <see cref="Page.NavigationTimeout"/> for the run.</summary>
	public TimeSpan? NavigationTimeout { get; init; }

	/// <summary>Host patterns the host allows on top of the journey's <c>allow</c> lines and the base URL's host.</summary>
	public IReadOnlyList<string> AllowedHosts { get; init; } = [];

	/// <summary>
	/// The one folder <c>file://</c> URLs may point into. Defaults to the base URL's folder when that is a file URL;
	/// otherwise file URLs are refused. A journey cannot widen it.
	/// </summary>
	public string? FileRoot { get; init; }

	/// <summary>
	/// The folder <c>screenshot</c> and <c>network har stop</c> write into; a journey cannot write outside it. Without
	/// it, those steps fail.
	/// </summary>
	public string? ArtifactsDirectory { get; init; }

	/// <summary>Take a screenshot of the viewport when a step fails, into <see cref="JourneyFailure.Screenshot"/> and, with an <see cref="ArtifactsDirectory"/>, <c>failure.png</c> there. Default: <c>true</c>.</summary>
	public bool ScreenshotOnFailure { get; init; } = true;

	/// <summary>Called before each step runs, e.g. to show progress.</summary>
	public Action<JourneyStep>? OnStep { get; init; }

	/// <summary>Resolves <c>$env:NAME</c> references; defaults to the process environment.</summary>
	public Func<string, string?> Secrets { get; init; } = Environment.GetEnvironmentVariable;

	/// <summary>
	/// Request headers every page of the run sends, such as one that marks the traffic as a monitor. A journey's own
	/// <c>set headers</c> adds to them (and wins for a header it names) rather than replacing them.
	/// </summary>
	public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

	/// <summary>
	/// The user agent of the run, for example one that tells the site it is a monitor. A journey's own <c>set useragent</c>
	/// replaces it; <c>set device</c> does not.
	/// </summary>
	public string? UserAgent { get; init; }

	/// <summary>
	/// The viewport the run starts with. Without one the browser's default applies, which a responsive site may treat as
	/// a phone. A journey's own <c>set viewport</c> or <c>set device</c> replaces it.
	/// </summary>
	public JourneyViewport? Viewport { get; init; }

	/// <summary>
	/// Refuse requests outside the allow list (on by default). Turning it off also lets <c>open</c> go anywhere.
	/// </summary>
	public bool GuardNetwork { get; init; } = true;

	/// <summary>
	/// The enabled feature flags, for <c>feature</c> blocks: a block whose conditions the run does not meet is skipped, and
	/// the result says so. Without them every <c>feature</c> block that needs a flag is skipped.
	/// </summary>
	public IReadOnlySet<string> Features { get; init; } = new HashSet<string>(StringComparer.Ordinal);

	/// <summary>The build type (<c>isolated</c>, <c>assembler</c>, <c>codex</c>), for <c>build-type:</c> conditions.</summary>
	public string? BuildType { get; init; }

	/// <summary>The environment's name (<c>prod</c>, <c>edge</c>), for <c>env:</c> conditions.</summary>
	public string? Env { get; init; }

	/// <summary>What <c>feature</c> blocks and <c>@requires</c> conditions are judged against.</summary>
	public JourneyContext Context => new(Features, BuildType, Env);

	/// <summary>
	/// Refuse every request that is not a read (anything but GET, HEAD and OPTIONS), so a run against a live site cannot
	/// change it. A refused write fails the step that caused it. Not tied to <see cref="GuardNetwork"/>.
	/// </summary>
	public bool ReadOnly { get; init; }

	/// <summary>
	/// URL globs (<see cref="Page.UrlGlob"/>: <c>**</c> anything, <c>*</c> anything but <c>/</c>) a read-only run may write
	/// to anyway, such as a telemetry endpoint.
	/// </summary>
	public IReadOnlyList<string> WritableUrls { get; init; } = [];

	internal NetworkGuardOptions GuardOptions()
	{
		var writable = WritableUrls.Select(Page.UrlGlob).ToArray();
		return new NetworkGuardOptions { ReadOnly = ReadOnly, AllowWrites = writable.Length == 0 ? null : uri => writable.Any(g => g.IsMatch(uri.AbsoluteUri)) };
	}

	internal JourneyAllowList AllowList(Journey journey)
	{
		var hosts = journey.AllowedHosts.Concat(AllowedHosts);
		if (BaseUrl is { Scheme: "http" or "https" } web)
			hosts = hosts.Append(web.IdnHost);
		// "file:///x/site/" and "file:///x/site/index.html" both give /x/site.
		var root = FileRoot ?? (BaseUrl is { IsFile: true } file ? Path.GetDirectoryName(file.LocalPath) : null);
		return new JourneyAllowList(GuardNetwork ? hosts : ["*"], root);
	}
}

/// <summary>The output of one checkpoint.</summary>
/// <param name="Line">The journey line.</param>
/// <param name="Command">The command as one normalised line (<see cref="JourneyStep.Text"/>).</param>
/// <param name="Url">The page's URL when the checkpoint ran.</param>
/// <param name="Output">What it read: an accessibility tree, a value, <c>true</c> or <c>false</c>.</param>
public sealed record Checkpoint(int Line, string Command, string Url, string Output)
{
	/// <summary>True for a <c>feature</c> block the run did not meet: <see cref="Output"/> then says why, and nothing came from the page.</summary>
	public bool IsSkip { get; init; }
}

/// <summary>The step a journey stopped at, and why.</summary>
/// <param name="Line">The journey line.</param>
/// <param name="Command">The command as one normalised line.</param>
/// <param name="Message">The error, in plain text.</param>
public sealed record JourneyFailure(int Line, string Command, string Message)
{
	/// <summary>The page's URL when the step failed, with secrets redacted. <c>null</c> when it could not be read.</summary>
	public string? Url { get; init; }

	/// <summary>A PNG of the viewport when the step failed (<see cref="JourneyOptions.ScreenshotOnFailure"/>). <c>null</c> when it could not be taken.</summary>
	public byte[]? Screenshot { get; init; }

	/// <summary>Where <see cref="Screenshot"/> was written, when the run has an artifacts folder.</summary>
	public string? ScreenshotPath { get; init; }

	/// <summary><c>file:line: command</c>, then the error, then the page and the screenshot as labelled lines.</summary>
	public string Describe(string source)
	{
		var text = new System.Text.StringBuilder($"{source}:{Line}: {Command}\n{Message}");
		if (Url is { Length: > 0 })
			text.Append("\nPage:       ").Append(Url);
		if (ScreenshotPath is { Length: > 0 })
			text.Append("\nScreenshot: ").Append(ScreenshotPath);
		return text.ToString();
	}
}

/// <summary>
/// What a run produced: every checkpoint that ran, the failure if a step failed, and the sub-resource requests the
/// allow list refused (a refused navigation is a failure instead).
/// </summary>
public sealed record JourneyResult(string Source, IReadOnlyList<Checkpoint> Checkpoints, JourneyFailure? Failure, IReadOnlyList<BlockedRequest> Blocked)
{
	/// <summary>Whether every step ran.</summary>
	public bool Passed => Failure is null;
}

/// <summary>Runs journeys against a page or a browser.</summary>
public static class JourneyRunner
{
	/// <summary>Runs <paramref name="journey"/> in a new context of <paramref name="browser"/>, disposed afterwards.</summary>
	public static async Task<JourneyResult> RunAsync(Journey journey, Browser browser, JourneyOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(browser);
		await using var context = await browser.NewContextAsync(cancellationToken).ConfigureAwait(false);
		var page = await context.NewPageAsync(cancellationToken).ConfigureAwait(false);
		return await RunAsync(journey, page, options, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Runs <paramref name="journey"/> on <paramref name="page"/>, step by step. A failing step stops the run and is
	/// returned, not thrown, together with the checkpoints that already ran.
	/// </summary>
	public static async Task<JourneyResult> RunAsync(Journey journey, Page page, JourneyOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(journey);
		ArgumentNullException.ThrowIfNull(page);
		options ??= new JourneyOptions();
		if (options.ActionTimeout is { } action)
			page.ActionTimeout = action;
		if (options.NavigationTimeout is { } navigation)
			page.NavigationTimeout = navigation;

		var run = new Run(journey, page, options, options.AllowList(journey), new JourneySecrets(options.Secrets));
		var checkpoints = new List<Checkpoint>();
		var blocked = new List<BlockedRequest>();
		// Without the guard, a guard that allows everything is still started when a step needs one (credentials).
		await using var guard = options.GuardNetwork || options.ReadOnly || journey.Steps.Any(s => s.Command is SetCommand { Setting: "credentials" })
			? await page.GuardNetworkAsync(options.GuardNetwork ? run.AllowList.Allows : _ => true, options.GuardOptions(), cancellationToken).ConfigureAwait(false)
			: null;
		run.Guard = guard;
		await ApplyDefaultsAsync(page, options, cancellationToken).ConfigureAwait(false);
		// The network is recorded only for journeys that read it.
		await using var network = journey.Steps.Any(s => s.Command is HarCommand or RequestsCommand or ExpectRequestCommand)
			? await page.RecordNetworkAsync(cancellationToken).ConfigureAwait(false)
			: null;
		run.Network = network;
		// Interactions are recorded only for journeys that read vitals.
		if (journey.Steps.Any(s => s.Command is PerformanceCommand { Kind: "vitals" }))
			await page.RecordInteractionsAsync(cancellationToken).ConfigureAwait(false);
		var context = options.Context;
		var skipped = new HashSet<int>();
		foreach (var step in journey.Steps)
		{
			// A block the run does not meet is skipped as a whole, and shows once, as a checkpoint saying why.
			if (step.Block is { } block && block.Unmet(context) is { } unmet)
			{
				if (skipped.Add(block.Line))
				{
					var skippedUrl = await page.UrlAsync(cancellationToken).ConfigureAwait(false);
					checkpoints.Add(new Checkpoint(block.Line, block.Text, run.Secrets.Redact(skippedUrl), $"skipped: {unmet}") { IsSkip = true });
				}

				continue;
			}

			string? failure;
			options.OnStep?.Invoke(step);
			if (step.Command is ActCommand or KeyboardCommand or DragCommand or ScrollCommand)
				run.ActionDocuments = page.DocumentLoads;
			try
			{
				var output = await RunStepAsync(run, step, cancellationToken).ConfigureAwait(false);
				failure = Blocked(guard, run.AllowList, blocked);
				// A navigation starts from the page it lands on.
				if (step.Command is OpenCommand or HistoryCommand)
					run.ActionDocuments = page.DocumentLoads;
				if (failure is null && output is not null)
				{
					var url = await page.UrlAsync(cancellationToken).ConfigureAwait(false);
					checkpoints.Add(new Checkpoint(step.Line, step.Text, run.Secrets.Redact(url), run.Secrets.Redact(output)));
				}
			}
			catch (Exception e) when (IsStepFailure(e, cancellationToken))
			{
				failure = Blocked(guard, run.AllowList, blocked) ?? e.Message;
			}

			if (failure is not null)
				return new JourneyResult(journey.Source, checkpoints, await FailureAsync(run, step, failure, cancellationToken).ConfigureAwait(false), blocked);
		}

		return new JourneyResult(journey.Source, checkpoints, null, blocked);
	}

	// The failure with what the page looked like: its URL, and a screenshot when asked for. Neither can fail the run again,
	// so a page that is gone (closed tab, lost connection) just leaves them out.
	private static async Task<JourneyFailure> FailureAsync(Run run, JourneyStep step, string message, CancellationToken cancellationToken)
	{
		var failure = new JourneyFailure(step.Line, step.Text, run.Secrets.Redact(message));
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(TimeSpan.FromSeconds(5));
		try
		{
			failure = failure with { Url = run.Secrets.Redact(await run.Page.UrlAsync(limit.Token).ConfigureAwait(false)) };
			if (!run.Options.ScreenshotOnFailure)
				return failure;
			var png = await run.Page.ScreenshotAsync(false, limit.Token).ConfigureAwait(false);
			failure = failure with { Screenshot = png };
			if (run.Options.ArtifactsDirectory is not null)
			{
				var file = ArtifactFile(run, "failure.png", "failure screenshot");
				await File.WriteAllBytesAsync(file, png, cancellationToken).ConfigureAwait(false);
				failure = failure with { ScreenshotPath = file };
			}
		}
		catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
		{
			// The page could not be read or drawn: the failure stands without it.
		}

		return failure;
	}

	// The run's viewport, user agent and headers, set before the first step so the journey starts as the harness wants.
	private static async Task ApplyDefaultsAsync(Page page, JourneyOptions options, CancellationToken cancellationToken)
	{
		if (options.Viewport is { } viewport)
			await page.SetViewportAsync(viewport.Width, viewport.Height, viewport.Scale, cancellationToken).ConfigureAwait(false);
		if (options.UserAgent is { } userAgent)
			await page.SetUserAgentAsync(userAgent, cancellationToken).ConfigureAwait(false);
		if (options.Headers.Count > 0)
			await page.SetExtraHeadersAsync(options.Headers, cancellationToken).ConfigureAwait(false);
	}

	private sealed record Run(Journey Journey, Page Page, JourneyOptions Options, JourneyAllowList AllowList, JourneySecrets Secrets)
	{
		public NetworkGuard? Guard { get; set; }

		public bool Offline { get; set; }

		public NetworkRecording? Network { get; set; }

		public int HarStart { get; set; } = -1;

		// The page's document count when the last action began (after a navigation, when it ended), for `expect same-document`.
		public int ActionDocuments { get; set; }
	}

	// A refused navigation fails the step; refused sub-resources are reported with the result.
	private static string? Blocked(NetworkGuard? guard, JourneyAllowList allowList, List<BlockedRequest> blocked)
	{
		if (guard is null)
			return null;
		string? failure = null;
		foreach (var request in guard.TakeBlocked())
		{
			if (request.RefusedWrite is { } method)
			{
				failure ??= $"Blocked {method} {request.Url}: this run is read-only, so only GET, HEAD and OPTIONS requests are sent (JourneyOptions.WritableUrls, or --writable on the CLI, allows a URL).";
				continue;
			}

			if (!request.IsNavigation)
			{
				blocked.Add(request);
				continue;
			}

			var reason = Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) ? allowList.Refusal(uri) : null;
			failure ??= $"The page navigated to {request.Url}, which is refused: {reason ?? "not an allowed URL"}.";
		}

		return failure;
	}

	// Anything a step can fail with, but not the caller cancelling.
	private static bool IsStepFailure(Exception e, CancellationToken cancellationToken) => e switch
	{
		OperationCanceledException when cancellationToken.IsCancellationRequested => false,
		LocatorTimeoutException or LocatorException or PageNavigationException or PageScriptException or PageSelectorException
			or TimeoutException or CdpException or CdpDisconnectedException or ArgumentException or JourneyParseException
			or InvalidOperationException => true,
		_ => false,
	};

	private static async Task<string?> RunStepAsync(Run run, JourneyStep step, CancellationToken ct)
	{
		var page = run.Page;
		switch (step.Command)
		{
			case OpenCommand open:
				var url = run.Journey.ResolveUrl(step, open.Url, run.Options.BaseUrl);
				if (Uri.TryCreate(url, UriKind.Absolute, out var opening) && run.AllowList.Refusal(opening) is { } refusal)
					throw new ArgumentException($"Opening {url} is refused: {refusal}.");
				await page.GotoAsync(url, ct).ConfigureAwait(false);
				return null;
			case HistoryCommand { Direction: "back" }:
				await page.BackAsync(ct).ConfigureAwait(false);
				return null;
			case HistoryCommand { Direction: "forward" }:
				await page.ForwardAsync(ct).ConfigureAwait(false);
				return null;
			case HistoryCommand:
				await page.ReloadAsync(ct).ConfigureAwait(false);
				return null;
			case ActCommand { Action: "upload" } upload:
				await upload.Target.On(page).UploadAsync(UploadFiles(run.Journey, upload.Values), ct).ConfigureAwait(false);
				return null;
			case ActCommand act:
				await ActAsync(act, act.Target.On(page), run.Secrets, ct).ConfigureAwait(false);
				return null;
			case PressCommand press:
				await page.PressAsync(press.Keys, ct).ConfigureAwait(false);
				return null;
			case KeyboardCommand { Mode: "type" } keyboard:
				await page.Keyboard.TypeAsync(run.Secrets.Resolve(keyboard.Text), ct).ConfigureAwait(false);
				return null;
			case KeyboardCommand keyboard:
				await page.Keyboard.InsertTextAsync(run.Secrets.Resolve(keyboard.Text), ct).ConfigureAwait(false);
				return null;
			case DragCommand drag:
				await drag.Source.On(page).DragToAsync(drag.Target.On(page), ct).ConfigureAwait(false);
				return null;
			case ScrollCommand scroll:
				var (dx, dy) = scroll.Direction switch
				{
					"up" => (0, -scroll.Pixels),
					"left" => (-scroll.Pixels, 0),
					"right" => (scroll.Pixels, 0),
					_ => (0, scroll.Pixels),
				};
				if (scroll.Within is { } within)
					await within.On(page).ScrollByAsync(dx, dy, ct).ConfigureAwait(false);
				else
					await page.ScrollAsync(dx, dy, ct).ConfigureAwait(false);
				return null;
			case WaitCommand { Url: { } glob }:
				await page.WaitForUrlAsync(glob, ct).ConfigureAwait(false);
				return null;
			case WaitCommand { LoadState: { } state }:
				await page.WaitForLoadStateAsync(state, ct).ConfigureAwait(false);
				return null;
			case WaitCommand { Milliseconds: { } ms }:
				await Task.Delay(ms, ct).ConfigureAwait(false);
				return null;
			case WaitCommand { Target: { } target }:
				await target.On(page).WaitAsync(LocatorState.Visible, ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "viewport" } viewport:
				await page.SetViewportAsync(viewport.Width, viewport.Height, viewport.Scale, ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "device" } device:
				await page.SetDeviceAsync(device.Device!, ct).ConfigureAwait(false);
				// A device brings its own user agent; the run's identity stays unless the journey sets another.
				if (run.Options.UserAgent is { } identity)
					await page.SetUserAgentAsync(identity, ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "geo" } geo:
				await page.SetGeolocationAsync(geo.Position.Latitude, geo.Position.Longitude, ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "offline" } offline:
				run.Offline = offline.Offline ?? !run.Offline;
				await page.SetOfflineAsync(run.Offline, ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "headers" } headers:
				await page.SetExtraHeadersAsync(MergeHeaders(run.Options.Headers, Headers(headers.Headers!)), ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "useragent" } userAgent:
				await page.SetUserAgentAsync(userAgent.UserAgent!, ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "credentials" } credentials:
				run.Guard!.UseCredentials(run.Secrets.Resolve(credentials.User!), run.Secrets.Resolve(credentials.Password!));
				return null;
			case SetCommand media:
				await page.SetColorSchemeAsync(media.Scheme!, ct).ConfigureAwait(false);
				return null;
			case AuditCommand audit:
				var violations = await page.AuditAsync(audit.Selector, ct).ConfigureAwait(false);
				if (audit.Strict && violations.Count > 0)
					throw new ArgumentException($"Accessibility violations: {string.Join(", ", violations.Select(v => $"{v.Rule} ({v.Nodes.Count})"))}.");
				return Nullean.Cdp.Accessibility.AccessibilityAudit.Format(violations);
			case HarCommand { Action: "start" }:
				run.HarStart = run.Network!.Requests.Count;
				return null;
			case HarCommand har:
				WriteHar(run, har.Path!);
				return null;
			case RequestsCommand { Clear: true }:
				run.Network!.Clear();
				if (run.HarStart >= 0)
					run.HarStart = 0;
				return null;
			case RequestsCommand requests:
				var listed = Filter(run.Network!.Requests, requests).ToList();
				if (requests.Strict && listed.Count > 0)
					throw new ArgumentException($"{listed.Count} request(s) listed with --strict:\n{string.Join('\n', listed.Take(10))}");
				return string.Join('\n', listed);
			case LinksCommand links:
				return await LinksAsync(run, links, ct).ConfigureAwait(false);
			case ScreenshotCommand shot:
				var png = shot.Selector is { } element
					? await page.Css(element).ScreenshotAsync(ct).ConfigureAwait(false)
					: await page.ScreenshotAsync(shot.Full, ct).ConfigureAwait(false);
				var file = ArtifactFile(run, shot.Path, "screenshot");
				await File.WriteAllBytesAsync(file, png, ct).ConfigureAwait(false);
				return null;
			case ClearLogCommand { Log: "console" }:
				page.ClearConsole();
				return null;
			case ClearLogCommand:
				page.ClearErrors();
				return null;
			case PerformanceCommand performance:
				return await PerformanceAsync(performance, page, ct).ConfigureAwait(false);
			case SnapshotCommand snapshot:
				return (await page.SnapshotAsync(snapshot.Options, ct).ConfigureAwait(false)).TrimEnd('\n');
			case ReadCommand read:
				return await ReadAsync(read, page, ct).ConfigureAwait(false);
			case ExpectCommand expect:
				await ExpectAsync(expect, page, run.Secrets, ct).ConfigureAwait(false);
				return null;
			case ExpectRequestCommand request:
				await ExpectRequestAsync(request, run, ct).ConfigureAwait(false);
				return null;
			case ExpectSameDocumentCommand sameDocument:
				await ExpectSameDocumentAsync(sameDocument, run, ct).ConfigureAwait(false);
				return null;
			default:
				throw new InvalidOperationException($"No runner for {step.Command.GetType().Name}.");
		}
	}

	// Raw readings, one per line; with budgets only whether each budgeted reading is within its limit, so the output
	// stays the same from run to run. A reading over its budget, or with no value, fails the step.
	private static async Task<string> PerformanceAsync(PerformanceCommand command, Page page, CancellationToken ct)
	{
		IReadOnlyList<(string Name, double? Value)> readings = command.Kind switch
		{
			"timings" => (await page.TimingsAsync(ct).ConfigureAwait(false)).Named,
			"vitals" => (await page.VitalsAsync(ct).ConfigureAwait(false)).Named,
			_ => [.. (await page.MetricsAsync(ct).ConfigureAwait(false)).Select(m => (m.Key, (double?)m.Value))],
		};
		var unit = command.Kind == "metrics" ? (Func<string, string>)(_ => "") : name => name == "cls" ? "" : "ms";
		if (command.Budgets.Count == 0)
			return string.Join('\n', readings.Select(r => $"{r.Name}: {PagePerformance.Format(r.Name, r.Value)}{(r.Value is null ? "" : unit(r.Name))}"));

		var lines = new List<string>();
		var over = new List<string>();
		foreach (var budget in command.Budgets)
		{
			var (name, reading) = readings.FirstOrDefault(r => string.Equals(r.Name, budget.Name, StringComparison.OrdinalIgnoreCase));
			var limit = $"{PagePerformance.Format(budget.Name, budget.Limit)}{unit(budget.Name)}";
			if (name is null)
				throw new ArgumentException($"{command.Kind} has no reading called '{budget.Name}'.");
			if (reading is not { } value)
				over.Add($"{budget.Name} has no value yet (budget {limit})");
			else if (value > budget.Limit)
				over.Add($"{budget.Name} {PagePerformance.Format(budget.Name, value)}{unit(budget.Name)} > {limit}");
			else
				lines.Add($"{budget.Name} ok (<= {limit})");
		}

		return over.Count > 0
			? throw new ArgumentException($"Over budget: {string.Join("; ", over)}.")
			: string.Join('\n', lines);
	}

	private static IEnumerable<NetworkRequest> Filter(IReadOnlyList<NetworkRequest> requests, RequestsCommand filter)
	{
		var glob = filter.Filter is { } f && f.Contains('*') ? Page.UrlGlob(f) : null;
		return requests.Where(r =>
			(filter.Filter is null || (glob?.IsMatch(r.Url) ?? r.Url.Contains(filter.Filter, StringComparison.Ordinal)))
			&& (filter.Types.Count == 0 || filter.Types.Contains(r.ResourceType, StringComparer.OrdinalIgnoreCase))
			&& (filter.Method is null || r.Method == filter.Method)
			&& (filter.Status is null || StatusMatches(r.Status, filter.Status))
			&& (!filter.Failed || Failed(r)));
	}

	// A failed request: 400 or more, or no answer, except what the allow list refused or a navigation cancelled.
	internal static bool Failed(NetworkRequest request) =>
		request.Status is >= 400
		|| (request.Status is null && request.Error is { } error
			&& !error.Contains("ERR_BLOCKED_BY_CLIENT", StringComparison.Ordinal) && !error.Contains("ERR_ABORTED", StringComparison.Ordinal));

	private static async Task<string> LinksAsync(Run run, LinksCommand command, CancellationToken ct)
	{
		var page = run.Page;
		var here = new Uri(await page.UrlAsync(ct).ConfigureAwait(false));
		var links = await page.LinksAsync(command.Selector, ct).ConfigureAwait(false);
		var check = new List<string>();
		int external = 0, refused = 0;
		foreach (var link in links)
		{
			var uri = new Uri(link);
			var sameSite = uri.IsFile ? here.IsFile : uri.GetLeftPart(UriPartial.Authority) == here.GetLeftPart(UriPartial.Authority);
			if (!sameSite && !command.External)
				external++;
			else if (!run.AllowList.Allows(uri))
				refused++;
			else
				check.Add(link);
		}

		var results = await page.CheckLinksAsync(check, cancellationToken: ct).ConfigureAwait(false);
		var broken = results.Where(r => r.Broken).ToList();
		var summary = $"links: {results.Count} checked, {broken.Count} broken"
			+ (external > 0 ? $", {external} on other sites not checked (--external)" : "")
			+ (refused > 0 ? $", {refused} outside the allow list" : "");
		var lines = (command.All ? results : broken).Select(r => $"{r.Outcome} {Display(run, here, r.Url)}");
		if (command.Strict && broken.Count > 0)
			throw new ArgumentException($"{broken.Count} broken link(s):\n{string.Join('\n', broken.Take(10).Select(r => $"{r.Outcome} {r.Url}"))}");
		return string.Join('\n', lines.Prepend(summary));
	}

	// Same-site links print without the origin (or, for files, relative to the base URL's folder), so transcripts
	// don't depend on the port or the machine.
	private static string Display(Run run, Uri here, string url)
	{
		var uri = new Uri(url);
		if (uri.IsFile)
		{
			var folder = run.Options.BaseUrl is { IsFile: true } baseUrl ? new Uri(baseUrl, ".").AbsoluteUri : null;
			return folder is not null && url.StartsWith(folder, StringComparison.Ordinal) ? "/" + url[folder.Length..] : url;
		}

		return uri.GetLeftPart(UriPartial.Authority) == here.GetLeftPart(UriPartial.Authority) ? uri.PathAndQuery : url;
	}

	internal static bool StatusMatches(int? status, string pattern)
	{
		if (status is not { } code)
			return false;
		if (pattern.EndsWith("xx", StringComparison.Ordinal))
			return code / 100 == pattern[0] - '0';
		var range = pattern.Split('-');
		return range.Length == 2
			? code >= int.Parse(range[0], CultureInfo.InvariantCulture) && code <= int.Parse(range[1], CultureInfo.InvariantCulture)
			: code == int.Parse(pattern, CultureInfo.InvariantCulture);
	}

	// HAR files go only inside the artifacts folder the host chose; secrets typed during the run are redacted.
	private static void WriteHar(Run run, string path)
	{
		if (run.HarStart < 0)
			throw new ArgumentException("network har stop without network har start.");
		var full = ArtifactFile(run, path, "network har stop");
		File.WriteAllText(full, NetworkRecording.ToHar(run.Network!.Requests.Skip(run.HarStart), run.Secrets.Redact));
		run.HarStart = -1;
	}

	// Files go only inside the artifacts folder the host chose.
	private static string ArtifactFile(Run run, string path, string command)
	{
		if (run.Options.ArtifactsDirectory is not { } artifacts)
			throw new ArgumentException($"{command} needs an artifacts folder to write into (JourneyOptions.ArtifactsDirectory, or --artifacts).");
		var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(artifacts)) + Path.DirectorySeparatorChar;
		var full = Path.GetFullPath(Path.Combine(folder, path));
		if (!full.StartsWith(folder, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException($"'{path}' is outside the artifacts folder.");
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		return full;
	}

	// The run's headers with the journey's on top: a header the journey names wins, the others stay.
	private static Dictionary<string, string> MergeHeaders(IReadOnlyDictionary<string, string> defaults, Dictionary<string, string> journey)
	{
		var merged = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
		foreach (var (name, value) in journey)
			merged[name] = value;
		return merged;
	}

	private static Dictionary<string, string> Headers(string json)
	{
		using var document = System.Text.Json.JsonDocument.Parse(json);
		return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
	}

	// Uploads read only files inside the journey's own folder: a shared journey must not pick files from elsewhere.
	private static string[] UploadFiles(Journey journey, IReadOnlyList<string> files)
	{
		if (!File.Exists(journey.Source))
			throw new ArgumentException("upload needs a journey loaded from a file: files are read relative to its folder.");
		var folder = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(journey.Source))!) + Path.DirectorySeparatorChar;
		var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
		return [.. files.Select(file =>
		{
			var full = Path.GetFullPath(Path.Combine(folder, file));
			if (!full.StartsWith(folder, comparison))
				throw new ArgumentException($"'{file}' is outside the journey's folder.");
			return File.Exists(full) ? full : throw new ArgumentException($"'{file}' does not exist next to the journey.");
		})];
	}

	private static async Task ActAsync(ActCommand act, Locator locator, JourneySecrets secrets, CancellationToken ct)
	{
		if (act.Action is "fill" or "type" && !JourneySecrets.IsReference(act.Values[0])
			&& string.Equals(await locator.AttributeAsync("type", ct).ConfigureAwait(false), "password", StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException($"{locator} is a password field: fill it with a secret reference ($env:NAME), not a literal.");
		var values = act.Values.Select(secrets.Resolve).ToArray();
		switch (act.Action)
		{
			case "click":
				await locator.ClickAsync(ct).ConfigureAwait(false);
				break;
			case "dblclick":
				await locator.DoubleClickAsync(ct).ConfigureAwait(false);
				break;
			case "focus":
				await locator.FocusAsync(ct).ConfigureAwait(false);
				break;
			case "scrollintoview":
				await locator.ScrollIntoViewAsync(ct).ConfigureAwait(false);
				break;
			case "hover":
				await locator.HoverAsync(ct).ConfigureAwait(false);
				break;
			case "check":
				await locator.CheckAsync(ct).ConfigureAwait(false);
				break;
			case "uncheck":
				await locator.UncheckAsync(ct).ConfigureAwait(false);
				break;
			case "fill":
				await locator.FillAsync(values[0], ct).ConfigureAwait(false);
				break;
			case "type":
				await locator.TypeAsync(values[0], ct).ConfigureAwait(false);
				break;
			case "select":
				await locator.SelectAsync(values, ct).ConfigureAwait(false);
				break;
			default:
				throw new InvalidOperationException($"No runner for action {act.Action}.");
		}
	}

	// A passing assertion records nothing; a failing one fails the step with the assertion's own message.
	private static async Task ExpectAsync(ExpectCommand expect, Page page, JourneySecrets secrets, CancellationToken ct)
	{
		var matcher = expect.Matcher.Expected is { } text ? expect.Matcher with { Expected = secrets.Resolve(text) } : expect.Matcher;
		var result = expect.Target is { } target
			? await target.On(page).CheckAsync(matcher, expect.Options, ct).ConfigureAwait(false)
			: await page.CheckAsync(matcher, expect.Options, ct).ConfigureAwait(false);
		if (!result.Passed)
			throw new ExpectationException(result);
	}

	// `expect request`: holds when a finished request matches the URL glob and every filter. Negated, it holds when none does and
	// none that could still match the URL is in flight, so a late failure is not missed.
	private static async Task ExpectRequestAsync(ExpectRequestCommand command, Run run, CancellationToken ct)
	{
		var timeout = command.Options.Timeout ?? run.Page.ActionTimeout;
		var glob = Page.UrlGlob(command.Url);
		var contains = command.BodyContains is { } text ? run.Secrets.Resolve(text) : null;
		var regex = command.BodyRegex is { } pattern ? new System.Text.RegularExpressions.Regex(run.Secrets.Resolve(pattern), System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)) : null;
		var types = command.Types ?? [];
		var started = System.Diagnostics.Stopwatch.StartNew();
		while (true)
		{
			var scoped = run.Network!.Requests
				.Where(r => glob.IsMatch(r.Url)
					&& (types.Count == 0 || types.Contains(r.ResourceType, StringComparer.OrdinalIgnoreCase))
					&& (command.Method is null || r.Method == command.Method))
				.ToList();
			var pending = scoped.Where(r => !r.IsFinished).ToList();
			var matching = new List<NetworkRequest>();
			foreach (var request in scoped.Where(r => r.IsFinished))
			{
				if ((command.Status is null || StatusMatches(request.Status, command.Status))
					&& (!command.Failed || Failed(request))
					&& await BodyMatchesAsync(request, command.LargerThan, contains, regex, ct).ConfigureAwait(false))
					matching.Add(request);
			}

			var holds = command.Options.Negate ? matching.Count == 0 && pending.Count == 0 : matching.Count > 0;
			if (holds)
				return;
			if (started.Elapsed >= timeout)
			{
				var describe = DescribeRequestExpectation(command);
				var received = command.Options.Negate
					? matching.Count > 0 ? "found " + ListRequests(matching, command) : $"{pending.Count} still in flight: " + ListRequests(pending, command)
					: scoped.Count == 0
						? $"no request with that URL among the {run.Network!.Requests.Count} so far"
						: $"{scoped.Count} with that URL, none matching the other filters" + (pending.Count > 0 ? $" ({pending.Count} still in flight)" : "") + ": " + ListRequests(scoped, command);
				throw new ExpectationException(new ExpectationResult(false, "the requests",
					(command.Options.Negate ? "include no request " : "include a request ") + describe, received, timeout, started.Elapsed));
			}

			await Task.Delay(command.Options.PollInterval, ct).ConfigureAwait(false);
		}
	}

	private static async Task<bool> BodyMatchesAsync(NetworkRequest request, long? largerThan, string? contains, System.Text.RegularExpressions.Regex? regex, CancellationToken ct)
	{
		if (largerThan is null && contains is null && regex is null)
			return true;
		var body = await request.GetPostDataAsync(ct).ConfigureAwait(false);
		if (largerThan is { } size && (body?.LongLength ?? 0) <= size)
			return false;
		if (contains is null && regex is null)
			return true;
		if (body is null)
			return false;
		var text = System.Text.Encoding.UTF8.GetString(body);
		return (contains is null || text.Contains(contains, StringComparison.Ordinal)) && (regex is null || regex.IsMatch(text));
	}

	// What was asked for, as the journey line said it. A body filter shows its text and size only, never the body.
	private static string DescribeRequestExpectation(ExpectRequestCommand c)
	{
		var parts = new List<string> { "matching \"" + c.Url + "\"" };
		if (c.Method is not null)
			parts.Add("--method " + c.Method);
		if (c.Types is { Count: > 0 } types)
			parts.Add("--type " + string.Join(',', types));
		if (c.Status is not null)
			parts.Add("--status " + c.Status);
		if (c.Failed)
			parts.Add("--failed");
		if (c.LargerThan is { } larger)
			parts.Add("--larger-than " + larger.ToString(CultureInfo.InvariantCulture) + " bytes");
		if (c.BodyContains is not null)
			parts.Add("--body-contains \"" + c.BodyContains + "\"");
		if (c.BodyRegex is not null)
			parts.Add("--body-regex \"" + c.BodyRegex + "\"");
		return string.Join(' ', parts);
	}

	private static string ListRequests(List<NetworkRequest> requests, ExpectRequestCommand command)
	{
		var shown = requests.Take(5).Select(r => command.LargerThan is not null && r.PostDataLength is { } length ? $"{r} [{length} bytes]" : r.ToString());
		return string.Join("; ", shown) + (requests.Count > 5 ? $"; and {requests.Count - 5} more" : "");
	}

	// `expect same-document`: no new document since the last action began. Negated, it waits for one.
	private static async Task ExpectSameDocumentAsync(ExpectSameDocumentCommand command, Run run, CancellationToken ct)
	{
		var timeout = command.Options.Timeout ?? run.Page.ActionTimeout;
		var started = System.Diagnostics.Stopwatch.StartNew();
		while (true)
		{
			var replaced = run.Page.DocumentLoads - run.ActionDocuments;
			if (replaced == 0 != command.Options.Negate)
				return;
			if (started.Elapsed >= timeout)
				throw new ExpectationException(new ExpectationResult(false, "the page",
					command.Options.Negate ? "have been replaced by a new document since the last action" : "still be the document the last action began on",
					replaced == 0 ? "the same document" : $"{replaced} new document(s) since the last action", timeout, started.Elapsed));
			await Task.Delay(command.Options.PollInterval, ct).ConfigureAwait(false);
		}
	}

	private static async Task<string> ReadAsync(ReadCommand read, Page page, CancellationToken ct)
	{
		var locator = read.Target?.On(page);
		return read.What switch
		{
			"title" => await page.TitleAsync(ct).ConfigureAwait(false),
			"console" => string.Join('\n', page.ConsoleMessages),
			"errors" => string.Join('\n', page.Errors),
			"url" => await page.UrlAsync(ct).ConfigureAwait(false),
			"text" => await locator!.TextAsync(ct).ConfigureAwait(false),
			"html" => await locator!.HtmlAsync(ct).ConfigureAwait(false),
			"value" => await locator!.ValueAsync(ct).ConfigureAwait(false),
			"count" => (await locator!.CountAsync(ct).ConfigureAwait(false)).ToString(CultureInfo.InvariantCulture),
			"box" => await locator!.BoxAsync(ct).ConfigureAwait(false),
			"styles" => await locator!.StylesAsync(ct).ConfigureAwait(false),
			"attr" => await locator!.AttributeAsync(read.Attribute!, ct).ConfigureAwait(false) ?? "null",
			"is visible" => Bool(await locator!.IsVisibleAsync(ct).ConfigureAwait(false)),
			"is enabled" => Bool(await locator!.IsEnabledAsync(ct).ConfigureAwait(false)),
			"is checked" => Bool(await locator!.IsCheckedAsync(ct).ConfigureAwait(false)),
			_ => throw new InvalidOperationException($"No runner for get {read.What}."),
		};

		static string Bool(bool value) => value ? "true" : "false";
	}
}
