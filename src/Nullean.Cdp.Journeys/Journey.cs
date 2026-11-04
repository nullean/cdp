using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nullean.Cdp.Accessibility;

namespace Nullean.Cdp.Journeys;

/// <summary>A problem on one line of a journey.</summary>
/// <param name="Line">The 1-based line number.</param>
/// <param name="Message">What is wrong, in plain text.</param>
public sealed record JourneyError(int Line, string Message);

/// <summary>A journey has errors; the message lists every one as <c>file:line: message</c>.</summary>
public sealed class JourneyParseException(string source, IReadOnlyList<JourneyError> errors)
	: Exception(string.Join('\n', errors.Select(e => $"{source}:{e.Line}: {e.Message}")))
{
	/// <summary>The file, or <c>journey</c> for text without one.</summary>
	public string File { get; } = source;

	/// <summary>Every error, in line order.</summary>
	public IReadOnlyList<JourneyError> Errors { get; } = errors;
}

/// <summary>Where a step finds its element.</summary>
/// <param name="Kind">CSS for plain commands; the locator for <c>find</c>.</param>
/// <param name="Value">The selector or locator value.</param>
/// <param name="Name">The accessible name, for <c>find role … --name</c>.</param>
/// <param name="Exact">Whether <c>--exact</c> was given.</param>
/// <param name="Index">For <c>find first|last|nth</c>: which match, from zero; <c>-1</c> is the last.</param>
public sealed record JourneyTarget(LocatorKind Kind, string Value, string? Name = null, bool Exact = false, int? Index = null)
{
	/// <summary>The locator on <paramref name="page"/>.</summary>
	public Locator On(Page page)
	{
		var locator = page.Locate(Kind, Value, Name, Exact);
		return Index is { } index ? locator.Nth(index) : locator;
	}
}

/// <summary>One parsed command. Checkpoints (<see cref="IsCheckpoint"/>) produce output; the rest act.</summary>
public abstract record JourneyCommand
{
	/// <summary>Whether this command records output for the transcript.</summary>
	public virtual bool IsCheckpoint => false;
}

/// <summary><c>open &lt;url&gt;</c>.</summary>
public sealed record OpenCommand(string Url) : JourneyCommand;

/// <summary><c>back</c>, <c>forward</c> or <c>reload</c>.</summary>
public sealed record HistoryCommand(string Direction) : JourneyCommand;

/// <summary>
/// An action on an element: click, dblclick, hover, focus, scrollintoview, fill, type, check, uncheck, select or upload.
/// </summary>
public sealed record ActCommand(string Action, JourneyTarget Target, IReadOnlyList<string> Values) : JourneyCommand;

/// <summary><c>press &lt;key&gt;</c>, on whatever has focus.</summary>
public sealed record PressCommand(string Keys) : JourneyCommand;

/// <summary>
/// <c>wait &lt;selector&gt;</c>, <c>wait &lt;ms&gt;</c>, <c>wait --text &lt;text&gt;</c>, <c>wait --url &lt;glob&gt;</c> or
/// <c>wait --load load|domcontentloaded|networkidle</c>.
/// </summary>
public sealed record WaitCommand(JourneyTarget? Target, int? Milliseconds, string? Url = null, string? LoadState = null) : JourneyCommand;

/// <summary>
/// <c>set viewport &lt;w&gt; &lt;h&gt; [scale]</c>, <c>set media dark|light</c>, <c>set device &lt;name&gt;</c>,
/// <c>set geo &lt;lat&gt; &lt;lng&gt;</c>, <c>set offline [on|off]</c>, <c>set headers &lt;json&gt;</c> or
/// <c>set credentials &lt;user&gt; &lt;password&gt;</c>.
/// </summary>
public sealed record SetCommand(string Setting, int Width = 0, int Height = 0, string? Scheme = null) : JourneyCommand
{
	/// <summary>The device pixel ratio for <c>set viewport</c>.</summary>
	public double Scale { get; init; } = 1;

	/// <summary>The device for <c>set device</c>.</summary>
	public string? Device { get; init; }

	/// <summary>The position for <c>set geo</c>.</summary>
	public (double Latitude, double Longitude) Position { get; init; }

	/// <summary>For <c>set offline</c>: on, off, or <c>null</c> to toggle.</summary>
	public bool? Offline { get; init; }

	/// <summary>The headers for <c>set headers</c>, as JSON text.</summary>
	public string? Headers { get; init; }

	/// <summary>The user for <c>set credentials</c>.</summary>
	public string? User { get; init; }

	/// <summary>The password for <c>set credentials</c>: always a secret reference.</summary>
	public string? Password { get; init; }
}

/// <summary>
/// <c>timings</c>, <c>vitals</c> or <c>metrics</c>, a checkpoint: raw readings, or with <c>--budget name=limit</c> only
/// whether each budgeted reading is within its limit (a reading over its limit fails the step).
/// </summary>
public sealed record PerformanceCommand(string Kind, IReadOnlyList<PerformanceBudget> Budgets) : JourneyCommand
{
	/// <inheritdoc />
	public override bool IsCheckpoint => true;

	/// <inheritdoc />
	public bool Equals(PerformanceCommand? other) =>
		other is not null && Kind == other.Kind && Budgets.SequenceEqual(other.Budgets);

	/// <inheritdoc />
	public override int GetHashCode() => HashCode.Combine(Kind, Budgets.Count);
}

/// <summary>A limit for one reading: <c>lcp=2500</c> means at most 2500 ms.</summary>
public sealed record PerformanceBudget(string Name, double Limit);

/// <summary><c>network har start</c> or <c>network har stop &lt;path&gt;</c>: records a HAR file into the artifacts folder.</summary>
public sealed record HarCommand(string Action, string? Path = null) : JourneyCommand;

/// <summary>
/// <c>network requests</c>, a checkpoint listing the requests since the journey started (or since <c>--clear</c>),
/// optionally filtered by URL, type, method or status. <c>--failed</c> keeps requests answered with 400 or more and
/// requests that got no answer, except those the allow list refused or a navigation cancelled; <c>--strict</c> fails
/// the step when any request is listed.
/// </summary>
public sealed record RequestsCommand(bool Clear, string? Filter, IReadOnlyList<string> Types, string? Method, string? Status, bool Failed = false, bool Strict = false) : JourneyCommand
{
	/// <inheritdoc />
	public override bool IsCheckpoint => !Clear;

	/// <inheritdoc />
	public bool Equals(RequestsCommand? other) =>
		other is not null && Clear == other.Clear && Filter == other.Filter && Types.SequenceEqual(other.Types) && Method == other.Method
		&& Status == other.Status && Failed == other.Failed && Strict == other.Strict;

	/// <inheritdoc />
	public override int GetHashCode() => HashCode.Combine(Clear, Filter, Method, Status, Failed, Strict);
}

/// <summary>
/// <c>links [-s selector] [--external] [--all] [--strict]</c>, a checkpoint: checks the page's links and lists the
/// broken ones (every one with <c>--all</c>). Links to other sites are checked only with <c>--external</c>, and only
/// those the allow list allows; <c>--strict</c> fails the step on a broken link.
/// </summary>
public sealed record LinksCommand(string? Selector, bool External, bool All, bool Strict) : JourneyCommand
{
	/// <inheritdoc />
	public override bool IsCheckpoint => true;
}

/// <summary>
/// <c>screenshot [selector] &lt;path&gt; [--full]</c>: writes a PNG of the viewport, the whole page or one element into
/// the artifacts folder. Not a checkpoint: pixels differ between machines.
/// </summary>
public sealed record ScreenshotCommand(string? Selector, string Path, bool Full) : JourneyCommand;

/// <summary>
/// <c>a11y [-s selector] [--strict]</c>, a checkpoint: our own accessibility rules (<c>AccessibilityAudit</c>);
/// <c>--strict</c> fails the step on any violation.
/// </summary>
public sealed record AuditCommand(string? Selector, bool Strict) : JourneyCommand
{
	/// <inheritdoc />
	public override bool IsCheckpoint => true;
}

/// <summary><c>console --clear</c> or <c>errors --clear</c>: forgets what the page logged so far.</summary>
public sealed record ClearLogCommand(string Log) : JourneyCommand;

/// <summary><c>drag &lt;source&gt; &lt;target&gt;</c>.</summary>
public sealed record DragCommand(JourneyTarget Source, JourneyTarget Target) : JourneyCommand;

/// <summary><c>scroll [up|down|left|right] [pixels] [-s selector]</c>.</summary>
public sealed record ScrollCommand(string Direction, int Pixels, JourneyTarget? Within) : JourneyCommand;

/// <summary><c>keyboard type &lt;text&gt;</c> or <c>keyboard inserttext &lt;text&gt;</c>, into whatever has focus.</summary>
public sealed record KeyboardCommand(string Mode, string Text) : JourneyCommand;

/// <summary><c>snapshot [-i] [-c] [-d n] [-s selector]</c>, a checkpoint.</summary>
public sealed record SnapshotCommand(AxSnapshotOptions Options) : JourneyCommand
{
	/// <inheritdoc />
	public override bool IsCheckpoint => true;
}

/// <summary><c>get …</c>, <c>is …</c> or <c>find … text</c>: a checkpoint that reads one value.</summary>
public sealed record ReadCommand(string What, JourneyTarget? Target, string? Attribute = null) : JourneyCommand
{
	/// <inheritdoc />
	public override bool IsCheckpoint => true;
}

/// <summary>One line of a journey that holds a command.</summary>
/// <param name="Line">The 1-based line number.</param>
/// <param name="Arguments">The line split into arguments, comments removed.</param>
/// <param name="Command">What it does.</param>
public sealed record JourneyStep(int Line, IReadOnlyList<string> Arguments, JourneyCommand Command)
{
	/// <summary>The command as one normalised line: no comment, single spaces, quoted only where needed.</summary>
	public string Text => CommandLine.Join(Arguments);
}

/// <summary>
/// A synthetic journey: one agent-browser command per line, from the subset this library runs. Blank lines and
/// <c>#</c> comments are ignored.
/// </summary>
public sealed partial class Journey
{
	private Journey(string source, IReadOnlyList<JourneyStep> steps, IReadOnlyList<string> allowedHosts)
	{
		Source = source;
		Steps = steps;
		AllowedHosts = allowedHosts;
	}

	/// <summary>The host patterns from the journey's <c>allow</c> lines.</summary>
	public IReadOnlyList<string> AllowedHosts { get; }

	/// <summary>The file the journey came from, or <c>journey</c>.</summary>
	public string Source { get; }

	/// <summary>The commands, in order.</summary>
	public IReadOnlyList<JourneyStep> Steps { get; }

	/// <summary>Reads and parses a journey file.</summary>
	public static Journey Load(string path) => Parse(System.IO.File.ReadAllText(path), path);

	/// <summary>Parses journey text. Every error is reported at once, with its line.</summary>
	/// <exception cref="JourneyParseException">The text has errors.</exception>
	public static Journey Parse(string text, string source = "journey")
	{
		ArgumentNullException.ThrowIfNull(text);
		var steps = new List<JourneyStep>();
		var errors = new List<JourneyError>();
		var allowed = new List<string>();
		var lines = text.Split('\n');
		for (var i = 0; i < lines.Length; i++)
		{
			var arguments = CommandLine.Split(lines[i].TrimEnd('\r'), out var splitError);
			if (arguments is null)
			{
				errors.Add(new JourneyError(i + 1, splitError!));
				continue;
			}

			if (arguments.Count == 0)
				continue;
			if (arguments[0] == "allow")
			{
				if (arguments.Count == 1)
					errors.Add(new JourneyError(i + 1, "usage: allow <host pattern...>, e.g. allow shop.test *.shop.test"));
				foreach (var pattern in arguments.Skip(1))
				{
					if (JourneyAllowList.IsValidPattern(pattern))
						allowed.Add(pattern);
					else
						errors.Add(new JourneyError(i + 1, $"'{pattern}' is not a host pattern: use a host name, *.domain, or *"));
				}

				continue;
			}

			try
			{
				steps.Add(new JourneyStep(i + 1, arguments, ParseCommand(arguments)));
			}
			catch (JourneySyntaxException e)
			{
				errors.Add(new JourneyError(i + 1, e.Message));
			}
		}

		if (errors.Count > 0)
			throw new JourneyParseException(source, errors);
		return new Journey(source, steps, allowed);
	}

	/// <summary>The commands journeys support, for error messages and docs.</summary>
	public static IReadOnlyList<string> SupportedCommands { get; } =
	[
		"allow <host pattern...>", "open <url>", "back", "forward", "reload",
		"click <selector>", "dblclick <selector>", "hover <selector>", "focus <selector>", "scrollintoview <selector>",
		"fill <selector> <text>", "type <selector> <text>", "check <selector>", "uncheck <selector>",
		"select <selector> <value...>", "upload <selector> <files...>", "drag <source> <target>",
		"scroll [up|down|left|right] [pixels] [-s <selector>]", "press <key>", "keyboard type|inserttext <text>",
		"find role|text|label|placeholder|alt|title|testid <value> | first|last <selector> | nth <n> <selector> [click|fill|check|hover|text] [text] [--name <name>] [--exact]",
		"wait <selector>|<ms>|--text <text>|--url <glob>|--load <state>",
		"set viewport <w> <h> [scale]", "set media dark|light", "set device <name>", "set geo <lat> <lng>",
		"set offline [on|off]", "set headers <json>", "set credentials <user> <password>",
		"snapshot [-i] [-c] [-u] [-d <n>] [-s <selector>]",
		"get text|html|value|count|box|styles <selector>", "get attr <selector> <name>", "get title", "get url",
		"console [--clear]", "errors [--clear]",
		"network requests [--clear] [--filter <url>] [--type <types>] [--method <method>] [--status <code>] [--failed] [--strict]",
		"links [-s <selector>] [--external] [--all] [--strict]", "screenshot [selector] <path.png> [--full]",
		"network har start", "network har stop <path>", "a11y [-s <selector>] [--strict]",
		"timings [--budget <name>=<ms>...]", "vitals [--budget <name>=<limit>...]", "metrics [--budget <name>=<limit>...]",
		"is visible|enabled|checked <selector>",
	];

	private sealed class JourneySyntaxException(string message) : Exception(message);

	[GeneratedRegex(@"^@e\d+$")]
	private static partial Regex Ref();

	[GeneratedRegex(@"^[A-Za-z][A-Za-z0-9+.\-]+:")]
	private static partial Regex Scheme();

	private static JourneySyntaxException Usage(string usage) => new($"usage: {usage}");

	private static JourneyCommand ParseCommand(IReadOnlyList<string> a)
	{
		var command = a[0];
		var rest = a.Skip(1).ToArray();
		return command switch
		{
			"open" or "goto" or "navigate" => rest.Length == 1 ? new OpenCommand(rest[0]) : throw Usage("open <url>"),
			"back" or "forward" or "reload" => rest.Length == 0 ? new HistoryCommand(command) : throw Usage(command),
			"click" or "dblclick" or "hover" or "focus" or "check" or "uncheck" => rest.Length == 1
				? new ActCommand(command, Css(rest[0]), [])
				: throw Usage($"{command} <selector>"),
			"scrollintoview" or "scrollinto" => rest.Length == 1 ? new ActCommand("scrollintoview", Css(rest[0]), []) : throw Usage($"{command} <selector>"),
			"upload" => rest.Length >= 2 ? new ActCommand("upload", Css(rest[0]), [.. rest[1..].Select(UploadPath)]) : throw Usage("upload <selector> <files...>"),
			"drag" => rest.Length == 2 ? new DragCommand(Css(rest[0]), Css(rest[1])) : throw Usage("drag <source> <target>"),
			"scroll" => ParseScroll(rest),
			"keyboard" => rest is ["type" or "inserttext", var text] ? new KeyboardCommand(rest[0], Value(text)) : throw Usage("keyboard type|inserttext <text>"),
			"fill" or "type" => rest.Length == 2 ? new ActCommand(command, Css(rest[0]), [Value(rest[1])]) : throw Usage($"{command} <selector> <text>"),
			"select" => rest.Length >= 2 ? new ActCommand(command, Css(rest[0]), [.. rest[1..].Select(Value)]) : throw Usage("select <selector> <value...>"),
			"press" or "key" => rest.Length == 1 ? new PressCommand(rest[0]) : throw Usage("press <key>"),
			"find" => ParseFind(rest),
			"wait" => ParseWait(rest),
			"set" => ParseSet(rest),
			"snapshot" => ParseSnapshot(rest),
			"get" => ParseGet(rest),
			"timings" or "vitals" or "metrics" => ParsePerformance(command, rest),
			"network" => ParseNetwork(rest),
			"a11y" => ParseAudit(rest),
			"links" => ParseLinks(rest),
			"screenshot" => ParseScreenshot(rest),
			"console" or "errors" => rest switch
			{
				[] => new ReadCommand(command, null),
				["--clear"] => new ClearLogCommand(command),
				_ => throw Usage($"{command} [--clear]"),
			},
			"is" => rest.Length == 2 && rest[0] is "visible" or "enabled" or "checked"
				? new ReadCommand("is " + rest[0], Css(rest[1]))
				: throw Usage("is visible|enabled|checked <selector>"),
			_ => throw new JourneySyntaxException(
				$"'{command}' is not supported in journeys. Supported: {string.Join(", ", SupportedCommands.Select(c => c.Split(' ')[0]).Distinct())}."),
		};
	}

	private static string Value(string value) => JourneySecrets.IsMalformedReference(value)
		? throw new JourneySyntaxException($"'{value}' is not a secret reference: write $env:NAME with letters, digits and _.")
		: value;

	// Upload paths are relative to the journey's folder and may not leave it; checked again when the step runs.
	private static string UploadPath(string path) =>
		Path.IsPathRooted(path) || path.Replace('\\', '/').Split('/').Contains("..")
			? throw new JourneySyntaxException($"'{path}' must be a path inside the journey's folder, relative to it.")
			: path;

	private static ScrollCommand ParseScroll(string[] rest)
	{
		const string usage = "scroll [up|down|left|right] [pixels] [-s <selector>]";
		string direction = "down";
		var pixels = 300;
		JourneyTarget? within = null;
		var positional = 0;
		for (var i = 0; i < rest.Length; i++)
		{
			if (rest[i] is "-s" or "--selector")
			{
				within = i + 1 < rest.Length ? Css(rest[++i]) : throw new JourneySyntaxException("-s needs a selector");
				continue;
			}

			if (positional == 0 && rest[i] is "up" or "down" or "left" or "right")
				direction = rest[i];
			else if (positional <= 1 && int.TryParse(rest[i], NumberStyles.None, CultureInfo.InvariantCulture, out var amount) && amount > 0)
			{
				pixels = amount;
				positional = 1;
			}
			else
				throw Usage(usage);
			positional++;
		}

		return new ScrollCommand(direction, pixels, within);
	}

	private static JourneyTarget Css(string selector)
	{
		if (Ref().IsMatch(selector))
			throw new JourneySyntaxException(
				$"'{selector}' is an agent-browser ref, which changes from run to run; use find role <role> --name <name>, find text, or a CSS selector.");
		return new JourneyTarget(LocatorKind.Css, selector);
	}

	private static JourneyCommand ParseFind(string[] rest)
	{
		const string usage = "find role|text|label|placeholder|alt|title|testid <value> | first|last <selector> | nth <n> <selector> [click|fill|check|hover|text] [text] [--name <name>] [--exact]";
		string? name = null;
		var exact = false;
		var positional = new List<string>();
		for (var i = 0; i < rest.Length; i++)
		{
			switch (rest[i])
			{
				case "--name" when i + 1 < rest.Length:
					name = rest[++i];
					break;
				case "--name":
					throw new JourneySyntaxException("--name needs a value");
				case "--exact":
					exact = true;
					break;
				default:
					positional.Add(rest[i]);
					break;
			}
		}

		if (positional.Count < 2)
			throw Usage(usage);
		var kind = positional[0] switch
		{
			"role" => LocatorKind.Role,
			"text" => LocatorKind.Text,
			"label" => LocatorKind.Label,
			"placeholder" => LocatorKind.Placeholder,
			"alt" => LocatorKind.Alt,
			"title" => LocatorKind.Title,
			"testid" => LocatorKind.TestId,
			"first" or "last" or "nth" => LocatorKind.Css,
			var other => throw new JourneySyntaxException($"unknown locator '{other}': {usage}"),
		};
		if (name is not null && kind != LocatorKind.Role)
			throw new JourneySyntaxException("--name only applies to find role");

		// find first <selector> …, find last <selector> …, find nth <n> <selector> …
		int? index = positional[0] switch
		{
			"first" => 0,
			"last" => -1,
			"nth" when positional.Count >= 3 && int.TryParse(positional[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) => n,
			"nth" => throw new JourneySyntaxException("usage: find nth <n> <selector> [action] [text]"),
			_ => null,
		};
		var skip = positional[0] == "nth" ? 1 : 0;
		positional = [.. positional.Skip(skip)];
		var target = index is null
			? new JourneyTarget(kind, positional[1], name, exact)
			: Css(positional[1]) with { Index = index };
		var action = positional.Count > 2 ? positional[2] : "click";
		return action switch
		{
			"click" or "check" or "hover" when positional.Count is 2 or 3 => new ActCommand(action, target, []),
			"fill" when positional.Count == 4 => new ActCommand("fill", target, [Value(positional[3])]),
			"fill" => throw new JourneySyntaxException("find … fill needs the text to fill"),
			"text" when positional.Count == 3 => new ReadCommand("text", target),
			"click" or "check" or "hover" or "text" => throw Usage(usage),
			_ => throw new JourneySyntaxException($"find does not support '{action}': use click, fill, check, hover or text"),
		};
	}

	private static WaitCommand ParseWait(string[] rest)
	{
		const string usage = "wait <selector>|<ms>|--text <text>|--url <glob>|--load load|domcontentloaded|networkidle";
		if (rest.Length == 2 && rest[0] == "--text")
			return new WaitCommand(new JourneyTarget(LocatorKind.Text, rest[1]), null);
		if (rest.Length == 2 && rest[0] == "--url")
			return new WaitCommand(null, null, Url: rest[1]);
		if (rest.Length == 2 && rest[0] == "--load")
			return rest[1] is "load" or "domcontentloaded" or "networkidle" ? new WaitCommand(null, null, LoadState: rest[1]) : throw Usage(usage);
		if (rest.Length >= 1 && rest[0] is "--fn" or "--download")
			throw new JourneySyntaxException($"wait {rest[0]} is not supported in journeys: it runs page script or saves files.");
		if (rest.Length != 1 || rest[0].StartsWith("--", StringComparison.Ordinal))
			throw Usage(usage);
		return int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
			? new WaitCommand(null, ms)
			: new WaitCommand(Css(rest[0]), null);
	}

	private static SetCommand ParseSet(string[] rest)
	{
		const string all = "set viewport <w> <h> [scale] | media dark|light | device <name> | geo <lat> <lng> | offline [on|off] | headers <json> | credentials <user> <password>";
		switch (rest)
		{
			case ["viewport", var width, var height, .. var scale] when scale.Length <= 1:
				if (int.TryParse(width, NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w > 0
					&& int.TryParse(height, NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h > 0)
				{
					if (scale.Length == 0)
						return new SetCommand("viewport", w, h);
					if (double.TryParse(scale[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var factor) && factor > 0)
						return new SetCommand("viewport", w, h) { Scale = factor };
				}

				throw Usage("set viewport <width> <height> [scale]");
			case ["viewport", ..]:
				throw Usage("set viewport <width> <height> [scale]");
			case ["media", "dark" or "light"]:
				return new SetCommand("media", Scheme: rest[1]);
			case ["media", ..]:
				throw Usage("set media dark|light");
			case ["device", var device]:
				return Devices.All.ContainsKey(device)
					? new SetCommand("device") { Device = device }
					: throw new JourneySyntaxException($"unknown device '{device}'; known: {string.Join(", ", Devices.All.Keys.Order(StringComparer.Ordinal))}");
			case ["geo", var lat, var lng]:
				return double.TryParse(lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude) && latitude is >= -90 and <= 90
					&& double.TryParse(lng, NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude) && longitude is >= -180 and <= 180
					? new SetCommand("geo") { Position = (latitude, longitude) }
					: throw Usage("set geo <latitude> <longitude>");
			case ["offline"]:
				return new SetCommand("offline");
			case ["offline", "on" or "off"]:
				return new SetCommand("offline") { Offline = rest[1] == "on" };
			case ["headers", var json]:
				return ValidHeaders(json) ? new SetCommand("headers") { Headers = json } : throw new JourneySyntaxException("set headers needs a JSON object of string values, e.g. '{\"x-test\": \"1\"}'");
			case ["credentials", var user, var password]:
				return JourneySecrets.IsReference(password)
					? new SetCommand("credentials") { User = Value(user), Password = password }
					: throw new JourneySyntaxException("set credentials needs a secret reference for the password ($env:NAME), not a literal");
			case [var other, ..] when other is not ("viewport" or "media" or "device" or "geo" or "offline" or "headers" or "credentials"):
				throw new JourneySyntaxException($"set {other} is not supported in journeys: {all}");
			default:
				throw Usage(all);
		}
	}

	internal static readonly IReadOnlyList<string> TimingNames = ["ttfb", "first-paint", "fcp", "dom-interactive", "dom-content-loaded", "load"];
	internal static readonly IReadOnlyList<string> VitalNames = ["ttfb", "fcp", "lcp", "cls", "inp"];

	private static PerformanceCommand ParsePerformance(string kind, string[] rest)
	{
		var usage = $"{kind} [--budget <name>=<limit>...]";
		var budgets = new List<PerformanceBudget>();
		var inBudget = false;
		foreach (var argument in rest)
		{
			if (argument == "--budget")
			{
				inBudget = true;
				continue;
			}

			var parts = argument.Split('=', 2);
			if (!inBudget || parts.Length != 2
				|| !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) || limit < 0)
				throw Usage(usage);
			var known = kind switch { "timings" => TimingNames, "vitals" => VitalNames, _ => null };
			if (known is not null && !known.Contains(parts[0]))
				throw new JourneySyntaxException($"{kind} has no '{parts[0]}'; budget one of: {string.Join(", ", known)}");
			budgets.Add(new PerformanceBudget(parts[0], limit));
		}

		return inBudget && budgets.Count == 0 ? throw Usage(usage) : new PerformanceCommand(kind, budgets);
	}

	private static JourneyCommand ParseNetwork(string[] rest)
	{
		const string usage = "network requests [--clear] [--filter <url>] [--type <types>] [--method <method>] [--status <code>] [--failed] [--strict] | network har start | network har stop <path>";
		switch (rest)
		{
			case ["har", "start"]:
				return new HarCommand("start");
			case ["har", "stop", var path]:
				return new HarCommand("stop", ArtifactPath(path));
			case ["har", "stop"]:
				throw new JourneySyntaxException("network har stop needs a path for the HAR file, relative to the artifacts folder");
			case ["requests", .. var options]:
				bool clear = false, failed = false, strict = false;
				string? filter = null, method = null, status = null;
				var types = new List<string>();
				for (var i = 0; i < options.Length; i++)
				{
					var value = i + 1 < options.Length ? options[i + 1] : null;
					switch (options[i])
					{
						case "--clear":
							clear = true;
							continue;
						case "--failed":
							failed = true;
							continue;
						case "--strict":
							strict = true;
							continue;
						case "--filter" when value is not null:
							filter = value;
							break;
						case "--type" when value is not null:
							types.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
							break;
						case "--method" when value is not null:
							method = value.ToUpperInvariant();
							break;
						case "--status" when value is not null && StatusPattern().IsMatch(value):
							status = value;
							break;
						default:
							throw Usage(usage);
					}

					i++;
				}

				return clear && (failed || strict)
					? throw Usage(usage)
					: new RequestsCommand(clear, filter, types, method, status, failed, strict);
			case ["route" or "unroute" or "request", ..]:
				throw new JourneySyntaxException($"network {rest[0]} is not supported in journeys: it changes or reads what the page receives outside the page.");
			default:
				throw Usage(usage);
		}
	}

	[GeneratedRegex(@"^(\d{3}|[1-5]xx|\d{3}-\d{3})$")]
	private static partial Regex StatusPattern();

	private static string ArtifactPath(string path) =>
		Path.IsPathRooted(path) || path.Replace('\\', '/').Split('/').Contains("..")
			? throw new JourneySyntaxException($"'{path}' must be a path inside the artifacts folder, relative to it.")
			: path;

	private static LinksCommand ParseLinks(string[] rest)
	{
		string? selector = null;
		bool external = false, all = false, strict = false;
		for (var i = 0; i < rest.Length; i++)
		{
			switch (rest[i])
			{
				case "-s" or "--selector" when i + 1 < rest.Length:
					selector = Css(rest[++i]).Value;
					break;
				case "--external":
					external = true;
					break;
				case "--all":
					all = true;
					break;
				case "--strict":
					strict = true;
					break;
				default:
					throw new JourneySyntaxException("usage: links [-s <selector>] [--external] [--all] [--strict]");
			}
		}

		return new LinksCommand(selector, external, all, strict);
	}

	private static ScreenshotCommand ParseScreenshot(string[] rest)
	{
		const string usage = "usage: screenshot [selector] <path.png> [--full]";
		var full = rest.Any(r => r is "--full" or "-f");
		var positional = rest.Where(r => r is not ("--full" or "-f")).ToArray();
		if (positional.Any(p => p.StartsWith("--", StringComparison.Ordinal)))
			throw new JourneySyntaxException(rest.Any(r => r is "--annotate") ? "screenshot --annotate is not supported in journeys." : usage);
		var (selector, path) = positional switch
		{
			[var p] => (null, p),
			[var sel, var p] => (Css(sel).Value, p),
			[] => throw new JourneySyntaxException("screenshot needs a path for the PNG, relative to the artifacts folder"),
			_ => throw new JourneySyntaxException(usage),
		};
		if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
			throw new JourneySyntaxException($"'{path}': screenshots are PNG files, so the path ends in .png.");
		if (selector is not null && full)
			throw new JourneySyntaxException("screenshot takes a selector or --full, not both.");
		return new ScreenshotCommand(selector, ArtifactPath(path), full);
	}

	private static AuditCommand ParseAudit(string[] rest)
	{
		string? selector = null;
		var strict = false;
		for (var i = 0; i < rest.Length; i++)
		{
			switch (rest[i])
			{
				case "-s" or "--selector" when i + 1 < rest.Length:
					selector = Css(rest[++i]).Value;
					break;
				case "--strict":
					strict = true;
					break;
				default:
					throw new JourneySyntaxException(rest[i].StartsWith("--tags", StringComparison.Ordinal)
						? "a11y --tags is not supported in journeys: these are our own rules, not axe-core's."
						: "usage: a11y [-s <selector>] [--strict]");
			}
		}

		return new AuditCommand(selector, strict);
	}

	private static bool ValidHeaders(string json)
	{
		try
		{
			using var document = System.Text.Json.JsonDocument.Parse(json);
			return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
				&& document.RootElement.EnumerateObject().All(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.String);
		}
		catch (System.Text.Json.JsonException)
		{
			return false;
		}
	}

	private static SnapshotCommand ParseSnapshot(string[] rest)
	{
		var options = new AxSnapshotOptions();
		for (var i = 0; i < rest.Length; i++)
		{
			var hasValue = i + 1 < rest.Length;
			switch (rest[i])
			{
				case "-i" or "--interactive":
					options = options with { Interactive = true };
					break;
				case "-c" or "--compact":
					options = options with { Compact = true };
					break;
				case "-u" or "--urls":
					options = options with { Urls = true };
					break;
				case "--delta" or "--full":
					throw new JourneySyntaxException($"snapshot {rest[i]} is not supported in journeys: a checkpoint records the whole tree, and the transcript diff shows what changed.");
				case "-d" or "--depth":
					if (!hasValue || !int.TryParse(rest[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var depth))
						throw new JourneySyntaxException("-d needs a number");
					options = options with { Depth = depth };
					i++;
					break;
				case "-s" or "--selector":
					if (!hasValue)
						throw new JourneySyntaxException("-s needs a selector");
					options = options with { Selector = Css(rest[++i]).Value };
					break;
				default:
					throw new JourneySyntaxException($"snapshot {rest[i]} is not supported in journeys; use -i, -c, -u, -d <n> or -s <selector>.");
			}
		}

		return new SnapshotCommand(options);
	}

	private static ReadCommand ParseGet(string[] rest)
	{
		const string usage = "get text|html|value|count|box|styles <selector> | get attr <selector> <name> | get title | get url";
		return rest switch
		{
			["title" or "url"] => new ReadCommand(rest[0], null),
			["text" or "html" or "value" or "count" or "box" or "styles", var selector] => new ReadCommand(rest[0], Css(selector)),
			["attr", var selector, var name] => new ReadCommand("attr", Css(selector), name),
			[var what, ..] when what is not ("title" or "url" or "text" or "html" or "value" or "count" or "attr" or "box" or "styles")
				=> throw new JourneySyntaxException($"get {what} is not supported in journeys: {usage}"),
			_ => throw Usage(usage),
		};
	}

	/// <summary>
	/// The journey as agent-browser's batch input: a JSON array with one array of arguments per step, relative
	/// <c>open</c> URLs resolved against <paramref name="baseUrl"/>. Pipe it to <c>agent-browser batch --bail</c>.
	/// </summary>
	/// <remarks>
	/// <c>allow</c> lines are not commands there; pass them as <c>--allowed-domains</c> (<see cref="AllowedHosts"/>).
	/// Secret references cannot be read by agent-browser, so a journey that uses them is refused unless
	/// <paramref name="secrets"/> resolves them, and the output then holds the secret values.
	/// </remarks>
	public string ToAgentBrowserBatch(Uri? baseUrl = null, Func<string, string?>? secrets = null)
	{
		var resolver = secrets is null ? null : new JourneySecrets(secrets);
		var unresolved = Steps.Where(s => s.Arguments.Any(JourneySecrets.IsReference)).ToArray();
		if (resolver is null && unresolved.Length > 0)
			throw new JourneyParseException(Source, [.. unresolved.Select(s =>
				new JourneyError(s.Line, "uses a secret reference, which agent-browser cannot read; pass a secret resolver to export the value"))]);

		var buffer = new ArrayBufferWriter<byte>();
		// The relaxed encoder writes \" rather than \u0022; the output is for people and agent-browser, never HTML.
		using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartArray();
			foreach (var step in Steps)
			{
				// timings, metrics and links are not agent-browser commands; vitals is, without our budgets.
				if (step.Command is PerformanceCommand { Kind: not "vitals" } or LinksCommand)
					continue;
				json.WriteStartArray();
				if (step.Command is PerformanceCommand)
					json.WriteStringValue("vitals");
				else if (step.Command is AuditCommand audit)
				{
					// agent-browser's a11y runs axe-core; --strict is ours.
					json.WriteStringValue("a11y");
					if (audit.Selector is { } scope)
					{
						json.WriteStringValue("--selector");
						json.WriteStringValue(scope);
					}
				}
				else if (step.Command is RequestsCommand { Failed: true } or RequestsCommand { Strict: true })
				{
					// --failed and --strict are ours: agent-browser gets the closest status filter.
					foreach (var argument in step.Arguments.Where(a => a is not ("--failed" or "--strict")))
						json.WriteStringValue(argument);
					if (step.Command is RequestsCommand { Failed: true, Status: null })
					{
						json.WriteStringValue("--status");
						json.WriteStringValue("400-599");
					}
				}
				else if (step.Command is OpenCommand open)
				{
					json.WriteStringValue("open");
					json.WriteStringValue(ResolveUrl(step, open.Url, baseUrl));
				}
				else if (step.Command is ActCommand { Action: "upload" } upload)
				{
					// agent-browser reads upload paths from its own working folder; journeys from theirs.
					json.WriteStringValue("upload");
					json.WriteStringValue(step.Arguments[1]);
					var folder = Path.GetDirectoryName(Path.GetFullPath(Source)) ?? ".";
					foreach (var file in upload.Values)
						json.WriteStringValue(Path.GetFullPath(Path.Combine(folder, file)));
				}
				else
				{
					foreach (var argument in step.Arguments)
						json.WriteStringValue(resolver is not null && JourneySecrets.IsReference(argument) ? resolver.Resolve(argument) : argument);
				}

				json.WriteEndArray();
			}

			json.WriteEndArray();
		}

		return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
	}

	/// <summary>
	/// An absolute URL as written; a relative one resolved against <paramref name="baseUrl"/>, which is then required.
	/// </summary>
	internal string ResolveUrl(JourneyStep step, string url, Uri? baseUrl)
	{
		// Only a URL that starts with a scheme is absolute: on Linux and macOS "/docs" would otherwise parse as file:///docs.
		if (Scheme().IsMatch(url) && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
			return absolute.OriginalString;
		if (baseUrl is null)
			throw new JourneyParseException(Source, [new JourneyError(step.Line, $"'{url}' is relative: give a base URL")]);
		return new Uri(baseUrl, url).AbsoluteUri;
	}
}
