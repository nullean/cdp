using System.Globalization;

namespace Nullean.Cdp.Journeys;

/// <summary>
/// <c>expect [--not] [--timeout ms] &lt;matcher&gt; &lt;target&gt; …</c>: an assertion that keeps checking until it holds or
/// times out (<see cref="Page.ActionTimeout"/> by default) and fails the step when it does not. It records nothing in
/// the transcript, so a passing assertion changes no verified file. <see cref="Target"/> is <c>null</c> for the page's
/// <c>title</c> and <c>url</c>.
/// </summary>
public sealed record ExpectCommand(Matcher Matcher, JourneyTarget? Target, ExpectOptions Options) : JourneyCommand;

public sealed partial class Journey
{
	private const string ExpectUsage =
		"expect [--not] [--timeout <ms>] visible|hidden|enabled|disabled|checked|unchecked|focused|editable|attached|empty <target>"
		+ " | text|value|class <target> <text> | attr <target> <name> [<text>] | count <target> <n> | title <text> | url <glob>"
		+ " [--contains] [--regex] [--ignore-case] [--inner-text]; <target> is a selector or find <kind> <value> [--name <name>] [--exact]";

	private static ExpectCommand ParseExpect(string[] rest)
	{
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

		return new ExpectCommand(matcher, target, new ExpectOptions
		{
			Negate = negate,
			IgnoreCase = ignoreCase,
			UseInnerText = innerText,
			Timeout = timeoutMs is { } t ? TimeSpan.FromMilliseconds(t) : null,
		});
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
