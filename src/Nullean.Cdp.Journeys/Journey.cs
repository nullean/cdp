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
public sealed record JourneyTarget(LocatorKind Kind, string Value, string? Name = null, bool Exact = false)
{
	/// <summary>The locator on <paramref name="page"/>.</summary>
	public Locator On(Page page) => page.Locate(Kind, Value, Name, Exact);
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

/// <summary>An action on an element: click, hover, fill, type, check, uncheck or select.</summary>
public sealed record ActCommand(string Action, JourneyTarget Target, IReadOnlyList<string> Values) : JourneyCommand;

/// <summary><c>press &lt;key&gt;</c>, on whatever has focus.</summary>
public sealed record PressCommand(string Keys) : JourneyCommand;

/// <summary><c>wait &lt;selector&gt;</c>, <c>wait &lt;ms&gt;</c> or <c>wait --text &lt;text&gt;</c>.</summary>
public sealed record WaitCommand(JourneyTarget? Target, int? Milliseconds) : JourneyCommand;

/// <summary><c>set viewport &lt;w&gt; &lt;h&gt;</c> or <c>set media dark|light</c>.</summary>
public sealed record SetCommand(string Setting, int Width = 0, int Height = 0, string? Scheme = null) : JourneyCommand;

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
	private Journey(string source, IReadOnlyList<JourneyStep> steps)
	{
		Source = source;
		Steps = steps;
	}

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
		return new Journey(source, steps);
	}

	/// <summary>The commands journeys support, for error messages and docs.</summary>
	public static IReadOnlyList<string> SupportedCommands { get; } =
	[
		"open <url>", "back", "forward", "reload",
		"click <selector>", "hover <selector>", "fill <selector> <text>", "type <selector> <text>",
		"check <selector>", "uncheck <selector>", "select <selector> <value...>", "press <key>",
		"find role|text|label|placeholder|alt|title|testid <value> [click|fill|check|hover|text] [text] [--name <name>] [--exact]",
		"wait <selector>|<ms>|--text <text>", "set viewport <width> <height>", "set media dark|light",
		"snapshot [-i] [-c] [-d <n>] [-s <selector>]",
		"get text|html|value|count <selector>", "get attr <selector> <name>", "get title", "get url",
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
			"click" or "hover" or "check" or "uncheck" => rest.Length == 1 ? new ActCommand(command, Css(rest[0]), []) : throw Usage($"{command} <selector>"),
			"fill" or "type" => rest.Length == 2 ? new ActCommand(command, Css(rest[0]), [rest[1]]) : throw Usage($"{command} <selector> <text>"),
			"select" => rest.Length >= 2 ? new ActCommand(command, Css(rest[0]), rest[1..]) : throw Usage("select <selector> <value...>"),
			"press" or "key" => rest.Length == 1 ? new PressCommand(rest[0]) : throw Usage("press <key>"),
			"find" => ParseFind(rest),
			"wait" => ParseWait(rest),
			"set" => ParseSet(rest),
			"snapshot" => ParseSnapshot(rest),
			"get" => ParseGet(rest),
			"is" => rest.Length == 2 && rest[0] is "visible" or "enabled" or "checked"
				? new ReadCommand("is " + rest[0], Css(rest[1]))
				: throw Usage("is visible|enabled|checked <selector>"),
			_ => throw new JourneySyntaxException(
				$"'{command}' is not supported in journeys. Supported: {string.Join(", ", SupportedCommands.Select(c => c.Split(' ')[0]).Distinct())}."),
		};
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
		const string usage = "find role|text|label|placeholder|alt|title|testid <value> [click|fill|check|hover|text] [text] [--name <name>] [--exact]";
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
			"first" or "last" or "nth" => throw new JourneySyntaxException(
				$"find {positional[0]} is not supported in journeys; use a CSS selector that matches one element."),
			var other => throw new JourneySyntaxException($"unknown locator '{other}': {usage}"),
		};
		if (name is not null && kind != LocatorKind.Role)
			throw new JourneySyntaxException("--name only applies to find role");
		var target = new JourneyTarget(kind, positional[1], name, exact);
		var action = positional.Count > 2 ? positional[2] : "click";
		return action switch
		{
			"click" or "check" or "hover" when positional.Count is 2 or 3 => new ActCommand(action, target, []),
			"fill" when positional.Count == 4 => new ActCommand("fill", target, [positional[3]]),
			"fill" => throw new JourneySyntaxException("find … fill needs the text to fill"),
			"text" when positional.Count == 3 => new ReadCommand("text", target),
			"click" or "check" or "hover" or "text" => throw Usage(usage),
			_ => throw new JourneySyntaxException($"find does not support '{action}': use click, fill, check, hover or text"),
		};
	}

	private static WaitCommand ParseWait(string[] rest)
	{
		if (rest.Length == 2 && rest[0] == "--text")
			return new WaitCommand(new JourneyTarget(LocatorKind.Text, rest[1]), null);
		if (rest.Length == 1 && rest[0].StartsWith("--", StringComparison.Ordinal))
			throw new JourneySyntaxException($"wait {rest[0]} is not supported in journeys; use wait <selector>, wait <ms> or wait --text <text>.");
		if (rest.Length != 1)
			throw Usage("wait <selector>|<ms>|--text <text>");
		return int.TryParse(rest[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ms)
			? new WaitCommand(null, ms)
			: new WaitCommand(Css(rest[0]), null);
	}

	private static SetCommand ParseSet(string[] rest)
	{
		if (rest.Length >= 1 && rest[0] == "viewport")
		{
			if (rest.Length == 3 && int.TryParse(rest[1], NumberStyles.None, CultureInfo.InvariantCulture, out var w) && w > 0
				&& int.TryParse(rest[2], NumberStyles.None, CultureInfo.InvariantCulture, out var h) && h > 0)
				return new SetCommand("viewport", w, h);
			throw Usage("set viewport <width> <height>");
		}

		if (rest.Length >= 1 && rest[0] == "media")
			return rest.Length == 2 && rest[1] is "dark" or "light" ? new SetCommand("media", Scheme: rest[1]) : throw Usage("set media dark|light");
		throw new JourneySyntaxException(rest.Length == 0
			? "usage: set viewport <width> <height> | set media dark|light"
			: $"set {rest[0]} is not supported in journeys; use set viewport or set media.");
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
					throw new JourneySyntaxException($"snapshot {rest[i]} is not supported in journeys; use -i, -c, -d <n> or -s <selector>.");
			}
		}

		return new SnapshotCommand(options);
	}

	private static ReadCommand ParseGet(string[] rest)
	{
		const string usage = "get text|html|value|count <selector> | get attr <selector> <name> | get title | get url";
		return rest switch
		{
			["title" or "url"] => new ReadCommand(rest[0], null),
			["text" or "html" or "value" or "count", var selector] => new ReadCommand(rest[0], Css(selector)),
			["attr", var selector, var name] => new ReadCommand("attr", Css(selector), name),
			[var what, ..] when what is not ("title" or "url" or "text" or "html" or "value" or "count" or "attr")
				=> throw new JourneySyntaxException($"get {what} is not supported in journeys: {usage}"),
			_ => throw Usage(usage),
		};
	}

	/// <summary>
	/// The journey as agent-browser's batch input: a JSON array with one array of arguments per step, relative
	/// <c>open</c> URLs resolved against <paramref name="baseUrl"/>. Pipe it to <c>agent-browser batch --bail</c>.
	/// </summary>
	public string ToAgentBrowserBatch(Uri? baseUrl = null)
	{
		var buffer = new ArrayBufferWriter<byte>();
		// The relaxed encoder writes \" rather than \u0022; the output is for people and agent-browser, never HTML.
		using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartArray();
			foreach (var step in Steps)
			{
				json.WriteStartArray();
				if (step.Command is OpenCommand open)
				{
					json.WriteStringValue("open");
					json.WriteStringValue(ResolveUrl(step, open.Url, baseUrl));
				}
				else
				{
					foreach (var argument in step.Arguments)
						json.WriteStringValue(argument);
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
