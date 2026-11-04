using System.Globalization;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp.Journeys;

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

	/// <summary>Called before each step runs, e.g. to show progress.</summary>
	public Action<JourneyStep>? OnStep { get; init; }

	/// <summary>Resolves <c>$env:NAME</c> references; defaults to the process environment.</summary>
	public Func<string, string?> Secrets { get; init; } = Environment.GetEnvironmentVariable;

	/// <summary>
	/// Refuse requests outside the allow list (on by default). Turning it off also lets <c>open</c> go anywhere.
	/// </summary>
	public bool GuardNetwork { get; init; } = true;

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
public sealed record Checkpoint(int Line, string Command, string Url, string Output);

/// <summary>The step a journey stopped at, and why.</summary>
/// <param name="Line">The journey line.</param>
/// <param name="Command">The command as one normalised line.</param>
/// <param name="Message">The error, in plain text.</param>
public sealed record JourneyFailure(int Line, string Command, string Message)
{
	/// <summary><c>file:line: command</c>, then the error.</summary>
	public string Describe(string source) => $"{source}:{Line}: {Command}\n{Message}";
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
		await using var guard = options.GuardNetwork || journey.Steps.Any(s => s.Command is SetCommand { Setting: "credentials" })
			? await page.GuardNetworkAsync(options.GuardNetwork ? run.AllowList.Allows : _ => true, cancellationToken).ConfigureAwait(false)
			: null;
		run.Guard = guard;
		foreach (var step in journey.Steps)
		{
			string? failure;
			options.OnStep?.Invoke(step);
			try
			{
				var output = await RunStepAsync(run, step, cancellationToken).ConfigureAwait(false);
				failure = Blocked(guard, run.AllowList, blocked);
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
				return new JourneyResult(journey.Source, checkpoints, new JourneyFailure(step.Line, step.Text, run.Secrets.Redact(failure)), blocked);
		}

		return new JourneyResult(journey.Source, checkpoints, null, blocked);
	}

	private sealed record Run(Journey Journey, Page Page, JourneyOptions Options, JourneyAllowList AllowList, JourneySecrets Secrets)
	{
		public NetworkGuard? Guard { get; set; }

		public bool Offline { get; set; }
	}

	// A refused navigation fails the step; refused sub-resources are reported with the result.
	private static string? Blocked(NetworkGuard? guard, JourneyAllowList allowList, List<BlockedRequest> blocked)
	{
		if (guard is null)
			return null;
		string? failure = null;
		foreach (var request in guard.TakeBlocked())
		{
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
				return null;
			case SetCommand { Setting: "geo" } geo:
				await page.SetGeolocationAsync(geo.Position.Latitude, geo.Position.Longitude, ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "offline" } offline:
				run.Offline = offline.Offline ?? !run.Offline;
				await page.SetOfflineAsync(run.Offline, ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "headers" } headers:
				await page.SetExtraHeadersAsync(Headers(headers.Headers!), ct).ConfigureAwait(false);
				return null;
			case SetCommand { Setting: "credentials" } credentials:
				run.Guard!.UseCredentials(run.Secrets.Resolve(credentials.User!), run.Secrets.Resolve(credentials.Password!));
				return null;
			case SetCommand media:
				await page.SetColorSchemeAsync(media.Scheme!, ct).ConfigureAwait(false);
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
