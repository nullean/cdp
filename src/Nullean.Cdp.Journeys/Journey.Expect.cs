using System.Globalization;
using System.Text.RegularExpressions;

namespace Nullean.Cdp.Journeys;

/// <summary>
/// <c>expect [--not] [--timeout ms] &lt;matcher&gt; &lt;target&gt; …</c>: an assertion that keeps checking until it holds or
/// times out (<see cref="Page.ActionTimeout"/> by default) and fails the step when it does not. It records nothing in
/// the transcript, so a passing assertion changes no verified file. <see cref="Target"/> is <c>null</c> for the page's
/// <c>title</c> and <c>url</c>.
/// </summary>
public sealed record ExpectCommand(Matcher Matcher, JourneyTarget? Target, ExpectOptions Options) : JourneyCommand;

/// <summary>
/// <c>expect [--not] [--timeout ms] request &lt;url glob&gt; [filters]</c>: some request the page made, once it is over, matches
/// the URL glob and every filter (the ones <c>network requests</c> takes, plus the request body's size and text). With
/// <c>--not</c>, no request does, and none matching the URL is still in flight. Like every <c>expect</c> it records nothing.
/// </summary>
public sealed record ExpectRequestCommand(
	string Url,
	ExpectOptions Options,
	string? Method = null,
	IReadOnlyList<string>? Types = null,
	string? Status = null,
	bool Failed = false,
	long? LargerThan = null,
	string? BodyContains = null,
	string? BodyRegex = null
) : JourneyCommand
{
	/// <inheritdoc />
	public bool Equals(ExpectRequestCommand? other) =>
		other is not null && Url == other.Url && Options == other.Options && Method == other.Method && (Types ?? []).SequenceEqual(
			other.Types ?? []
		) && Status == other.Status && Failed == other.Failed && LargerThan == other.LargerThan && BodyContains == other.BodyContains && BodyRegex == other.BodyRegex;

	/// <inheritdoc />
	public override int GetHashCode() => HashCode.Combine(Url, Options, Method, Status, Failed, LargerThan, BodyContains, BodyRegex);
}

/// <summary>
/// <c>expect [--not] [--timeout ms] same-document</c>: the page is still on the document it was on when the last action began
/// (a click, a keypress, a navigation, …): nothing replaced it, as a boosted htmx swap or <c>pushState</c> does not. With
/// <c>--not</c>, it was replaced. Like every <c>expect</c> it records nothing.
/// </summary>
public sealed record ExpectSameDocumentCommand(ExpectOptions Options) : JourneyCommand;

public sealed partial class Journey
{
	private const string ExpectUsage = "expect [--not] [--timeout <ms>] visible|hidden|enabled|disabled|checked|unchecked|focused|editable|attached|empty <target>" +
		" | text|value|class <target> <text> | attr <target> <name> [<text>] | count <target> <n> | title <text> | url <glob>" +
		" [--contains] [--regex] [--ignore-case] [--inner-text]; <target> is a selector or find <kind> <value> [--name <name>] [--exact];" +
		" about the page: request <url glob> [--method <m>] [--type <types>] [--status <code>] [--failed] [--larger-than <size>] [--body-contains <text>] [--body-regex <pattern>] | same-document";

	private static JourneyCommand ParseExpect(string[] rest)
	{
		// The page's network and document are not an element: request and same-document read their own arguments.
		var word = ExpectWord(rest);
		if (word == "request")
			return ParseExpectRequest(rest);
		if (word == "same-document")
			return ParseExpectSameDocument(rest);

		var (negate, contains, regex, ignoreCase, innerText) = (false, false, false, false, false);
		int? timeoutMs = null;
		string? name = null;
		var exact = false;
		var positional = new List<string>();
		for (var i = 0; i < rest.Length; i++)
		{
			switch (rest[i])
			{
				case "--not":
					negate = true;
					break;
				case "--contains":
					contains = true;
					break;
				case "--regex":
					regex = true;
					break;
				case "--ignore-case":
					ignoreCase = true;
					break;
				case "--inner-text":
					innerText = true;
					break;
				case "--exact":
					exact = true;
					break;
				case "--name" when i + 1 < rest.Length:
					name = rest[++i];
					break;
				case "--name":
					throw new JourneySyntaxException("--name needs a value");
				case "--timeout":
					if (i + 1 >= rest.Length || !int.TryParse(rest[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms <= 0)
						throw new JourneySyntaxException("--timeout needs a number of milliseconds");
					timeoutMs = ms;
					i++;
					break;
				case var flag when flag.StartsWith("--", StringComparison.Ordinal):
					throw new JourneySyntaxException($"expect {flag} is not supported in journeys: {ExpectUsage}");
				default:
					positional.Add(rest[i]);
					break;
			}
		}

		if (positional.Count == 0)
			throw Usage(ExpectUsage);
		if (contains && regex)
			throw new JourneySyntaxException("expect takes --contains or --regex, not both.");

		var kind = positional[0] switch
		{
			"visible" => MatcherKind.Visible,
			"hidden" => MatcherKind.Hidden,
			"enabled" => MatcherKind.Enabled,
			"disabled" => MatcherKind.Disabled,
			"checked" => MatcherKind.Checked,
			"unchecked" => MatcherKind.Unchecked,
			"focused" => MatcherKind.Focused,
			"editable" => MatcherKind.Editable,
			"attached" => MatcherKind.Attached,
			"empty" => MatcherKind.Empty,
			"text" => MatcherKind.Text,
			"value" => MatcherKind.Value,
			"attr" => MatcherKind.Attribute,
			"class" => MatcherKind.Class,
			"count" => MatcherKind.Count,
			"title" => MatcherKind.Title,
			"url" => MatcherKind.Url,
			var other => throw new JourneySyntaxException($"expect {other} is not supported in journeys: {ExpectUsage}"),
		};
		var args = positional.Skip(1).ToList();

		// The target: a selector or a find locator, for every matcher but the page's title and url.
		JourneyTarget? target = null;
		if (kind is not (MatcherKind.Title or MatcherKind.Url))
		{
			if (args.Count == 0)
				throw Usage(ExpectUsage);
			target = ParseExpectTarget(args, name, exact);
		}
		else if (name is not null || exact)
			throw new JourneySyntaxException($"--name and --exact apply to a find target, and expect {positional[0]} is about the page.");

		var match = regex ? TextMatch.Regex : contains ? TextMatch.Contains : TextMatch.Exact;
		var flagsFor = kind switch
		{
			MatcherKind.Text or MatcherKind.Value or MatcherKind.Class or MatcherKind.Attribute or MatcherKind.Title or MatcherKind.Url => true,
			_ => false,
		};
		if ((contains || regex) && !flagsFor)
			throw new JourneySyntaxException($"--contains and --regex do not apply to expect {positional[0]}.");
		if (ignoreCase && kind is not (MatcherKind.Text or MatcherKind.Attribute or MatcherKind.Url))
			throw new JourneySyntaxException("--ignore-case applies to expect text, attr and url.");
		if (innerText && kind != MatcherKind.Text)
			throw new JourneySyntaxException("--inner-text applies to expect text.");

		Matcher matcher;
		switch (kind)
		{
			case MatcherKind.Text or MatcherKind.Value or MatcherKind.Class or MatcherKind.Title or MatcherKind.Url:
				if (args.Count != 1)
					throw Usage(ExpectUsage);
				var expected = Value(args[0]);
				matcher = kind switch
				{
					MatcherKind.Text => Matcher.Text(expected, match),
					MatcherKind.Value => Matcher.Value(expected, match),
					MatcherKind.Class => Matcher.Class(expected, match),
					MatcherKind.Title => Matcher.Title(expected, match),
					_ => Matcher.Url(expected, match),
				};
				break;
			case MatcherKind.Attribute:
				if (args.Count is not (1 or 2))
					throw Usage(ExpectUsage);
				if (args.Count == 1 && (contains || regex))
					throw new JourneySyntaxException("--contains and --regex need the attribute's expected value: expect attr <target> <name> <text>.");
				matcher = Matcher.Attribute(args[0], args.Count == 2 ? Value(args[1]) : null, match);
				break;
			case MatcherKind.Count:
				if (args.Count != 1 || !int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
					throw Usage(ExpectUsage);
				matcher = Matcher.Count(count);
				break;
			default:
				if (args.Count != 0)
					throw Usage(ExpectUsage);
				matcher = new Matcher(kind);
				break;
		}

		if (match == TextMatch.Regex && matcher.Expected is { } pattern)
		{
			try
			{
				_ = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
			}
			catch (ArgumentException e)
			{
				throw new JourneySyntaxException($"'{pattern}' is not a valid regular expression: {e.Message}");
			}
		}

		return new ExpectCommand(
			matcher,
			target,
			new ExpectOptions
			{
				Negate = negate,
				IgnoreCase = ignoreCase,
				UseInnerText = innerText,
				Timeout = timeoutMs is { } t ? TimeSpan.FromMilliseconds(t) : null,
			}
		);
	}

	// The matcher word: the first argument that is not --not or --timeout and its number.
	private static string? ExpectWord(string[] rest)
	{
		for (var i = 0; i < rest.Length; i++)
		{
			if (rest[i] == "--timeout")
				i++;
			else if (rest[i] != "--not")
				return rest[i];
		}

		return null;
	}

	private const string ExpectRequestUsage =
		"expect [--not] [--timeout <ms>] request <url glob> [--method <m>] [--type <types>] [--status <code>] [--failed] [--larger-than <size>] [--body-contains <text>] [--body-regex <pattern>]";

	private static ExpectRequestCommand ParseExpectRequest(string[] rest)
	{
		var negate = false;
		var failed = false;
		int? timeoutMs = null;
		string? method = null, status = null, contains = null, regex = null, url = null;
		long? larger = null;
		var types = new List<string>();
		var seenWord = false;
		for (var i = 0; i < rest.Length; i++)
		{
			var value = i + 1 < rest.Length ? rest[i + 1] : null;
			switch (rest[i])
			{
				case "--not":
					negate = true;
					continue;
				case "--failed":
					failed = true;
					continue;
				case "--timeout":
					if (value is null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms <= 0)
						throw new JourneySyntaxException("--timeout needs a number of milliseconds");
					timeoutMs = ms;
					break;
				case "--method" when value is not null:
					method = value.ToUpperInvariant();
					break;
				case "--type" when value is not null:
					types.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
					break;
				case "--status" when value is not null && StatusPattern().IsMatch(value):
					status = value;
					break;
				case "--larger-than" when value is not null:
					larger = ParseSize(value);
					break;
				case "--body-contains" when value is not null:
					contains = Value(value);
					break;
				case "--body-regex" when value is not null:
					regex = Value(value);
					break;
				case var flag when flag.StartsWith("--", StringComparison.Ordinal):
					throw Usage(ExpectRequestUsage);
				case var word when !seenWord && word == "request":
					seenWord = true;
					continue;
				case var glob when url is null:
					url = glob;
					continue;
				default:
					throw Usage(ExpectRequestUsage);
			}

			i++;
		}

		if (url is null)
			throw Usage(ExpectRequestUsage);
		if (contains is not null && regex is not null)
			throw new JourneySyntaxException("expect request takes --body-contains or --body-regex, not both.");
		if (regex is not null && !JourneySecrets.IsReference(regex))
		{
			try
			{
				_ = new System.Text.RegularExpressions.Regex(regex, System.Text.RegularExpressions.RegexOptions.CultureInvariant);
			}
			catch (ArgumentException e)
			{
				throw new JourneySyntaxException($"'{regex}' is not a valid regular expression: {e.Message}");
			}
		}

		return new ExpectRequestCommand(
			url,
			new ExpectOptions { Negate = negate, Timeout = timeoutMs is { } t ? TimeSpan.FromMilliseconds(t) : null },
			method,
			types,
			status,
			failed,
			larger,
			contains,
			regex
		);
	}

	// A size in bytes: 2048, 32kb, 1.5mb (1 kb is 1024 bytes).
	private static long ParseSize(string text)
	{
		var match = SizePattern().Match(text);
		if (!match.Success || !double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
			throw new JourneySyntaxException($"'{text}' is not a size: write a number of bytes, or 32kb, 1.5mb.");
		var unit = match.Groups[2].Value.ToLowerInvariant() switch
		{
			"kb" => 1024d,
			"mb" => 1024d * 1024,
			_ => 1d,
		};
		return (long)Math.Round(number * unit, MidpointRounding.AwayFromZero);
	}

	[GeneratedRegex(@"^(\d+(?:\.\d+)?)\s*(b|kb|mb)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex SizePattern();

	private static ExpectSameDocumentCommand ParseExpectSameDocument(string[] rest)
	{
		var negate = false;
		int? timeoutMs = null;
		var seenWord = false;
		for (var i = 0; i < rest.Length; i++)
		{
			switch (rest[i])
			{
				case "--not":
					negate = true;
					break;
				case "--timeout":
					if (i + 1 >= rest.Length || !int.TryParse(rest[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var ms) || ms <= 0)
						throw new JourneySyntaxException("--timeout needs a number of milliseconds");
					timeoutMs = ms;
					i++;
					break;
				case "same-document" when !seenWord:
					seenWord = true;
					break;
				default:
					throw Usage("expect [--not] [--timeout <ms>] same-document");
			}
		}

		return new ExpectSameDocumentCommand(new ExpectOptions { Negate = negate, Timeout = timeoutMs is { } t ? TimeSpan.FromMilliseconds(t) : null });
	}

	// Consumes the target from the front of args: a selector, or find <kind> <value>, find first|last <selector>,
	// find nth <n> <selector>. What is left are the matcher's own arguments.
	private static JourneyTarget ParseExpectTarget(List<string> args, string? name, bool exact)
	{
		if (args[0] != "find")
		{
			if (name is not null || exact)
				throw new JourneySyntaxException("--name and --exact apply to a find target: expect <matcher> find role button --name Save.");
			var selector = args[0];
			args.RemoveAt(0);
			return Css(selector);
		}

		const string findUsage = "expect <matcher> find role|text|label|placeholder|alt|title|testid <value> | first|last <selector> | nth <n> <selector>";
		if (args.Count < 3)
			throw Usage(findUsage);

		var kindWord = args[1];
		LocatorKind? kind = kindWord switch
		{
			"role" => LocatorKind.Role,
			"text" => LocatorKind.Text,
			"label" => LocatorKind.Label,
			"placeholder" => LocatorKind.Placeholder,
			"alt" => LocatorKind.Alt,
			"title" => LocatorKind.Title,
			"testid" => LocatorKind.TestId,
			"first" or "last" or "nth" => LocatorKind.Css,
			_ => null,
		};
		if (kind is null)
			throw new JourneySyntaxException($"unknown locator '{kindWord}': {findUsage}");
		if (name is not null && kind != LocatorKind.Role)
			throw new JourneySyntaxException("--name only applies to find role");

		JourneyTarget target;
		switch (kindWord)
		{
			case "first" or "last":
				target = Css(args[2]) with { Index = kindWord == "first" ? 0 : -1 };
				args.RemoveRange(0, 3);
				break;
			case "nth":
				if (args.Count < 4 || !int.TryParse(args[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
					throw new JourneySyntaxException("usage: expect <matcher> find nth <n> <selector>");
				target = Css(args[3]) with { Index = n };
				args.RemoveRange(0, 4);
				break;
			default:
				target = new JourneyTarget(kind.Value, args[2], name, exact);
				args.RemoveRange(0, 3);
				break;
		}

		return target;
	}
}
