using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Nullean.Cdp;

/// <summary>What one check saw: enough for any <see cref="Matcher"/> to decide, and to say what it saw.</summary>
internal readonly record struct Observation(bool Found, bool Flag = false, string? Text = null, int Count = 0);

// The engine behind Locator.CheckAsync and Page CheckAsync: poll an observation until the matcher holds or time runs out.
// It knows nothing about journeys or test frameworks; adapters report the ExpectationResult it returns.
internal static partial class Expectations
{
	private const string NoMatch = "no element matches";
	private const int MaxReceived = 200;

	public static async Task<ExpectationResult> RunAsync(
		Matcher matcher,
		ExpectOptions options,
		TimeSpan defaultTimeout,
		string subject,
		Func<CancellationToken, Task<Observation>> observe,
		CancellationToken cancellationToken
	)
	{
		ArgumentNullException.ThrowIfNull(matcher);
		ArgumentNullException.ThrowIfNull(options);
		Validate(matcher);
		var timeout = options.Timeout ?? defaultTimeout;
		var started = Stopwatch.StartNew();
		while (true)
		{
			var observation = await observe(cancellationToken).ConfigureAwait(false);
			var (matched, received) = Evaluate(matcher, options, observation);
			// Playwright's rule: only visibility, attachment and counts can be asserted about nothing; every other matcher
			// needs an element, even negated (`--not enabled` on a missing element keeps failing).
			var passed = (observation.Found || !NeedsElement(matcher.Kind)) && matched != options.Negate;
			if (passed)
				return new ExpectationResult(true, subject, matcher.Describe(options.Negate), null, timeout, started.Elapsed);
			if (started.Elapsed >= timeout)
				return new ExpectationResult(false, subject, matcher.Describe(options.Negate), received, timeout, started.Elapsed);
			await Task.Delay(options.PollInterval, cancellationToken).ConfigureAwait(false);
		}
	}

	private static bool NeedsElement(MatcherKind kind) =>
		kind is not (MatcherKind.Visible or MatcherKind.Hidden or MatcherKind.Attached or MatcherKind.Count or MatcherKind.Title or MatcherKind.Url);

	private static void Validate(Matcher matcher)
	{
		if (matcher.Match == TextMatch.Regex && matcher.Expected is { } pattern)
		{
			try
			{
				_ = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
			}
			catch (ArgumentException e)
			{
				throw new ArgumentException($"'{pattern}' is not a valid regular expression: {e.Message}", nameof(matcher), e);
			}
		}
	}

	// Whether the matcher holds for what was seen (before Negate is applied), and a phrase for what was seen.
	internal static (bool Matched, string Received) Evaluate(Matcher m, ExpectOptions o, Observation seen)
	{
		var text = seen.Text ?? string.Empty;
		return m.Kind switch
		{
			MatcherKind.Visible => (seen.Found && seen.Flag, seen.Found ? (seen.Flag ? "visible" : "not visible") : NoMatch),
			MatcherKind.Hidden => (!seen.Found || !seen.Flag, seen.Found ? (seen.Flag ? "visible" : "not visible") : NoMatch),
			MatcherKind.Enabled => (seen.Found && seen.Flag, seen.Found ? (seen.Flag ? "enabled" : "disabled") : NoMatch),
			MatcherKind.Disabled => (seen.Found && !seen.Flag, seen.Found ? (seen.Flag ? "enabled" : "disabled") : NoMatch),
			MatcherKind.Checked => (seen.Found && seen.Flag, seen.Found ? (seen.Flag ? "checked" : "unchecked") : NoMatch),
			MatcherKind.Unchecked => (seen.Found && !seen.Flag, seen.Found ? (seen.Flag ? "checked" : "unchecked") : NoMatch),
			MatcherKind.Focused => (seen.Found && seen.Flag, seen.Found ? (seen.Flag ? "focused" : "not focused") : NoMatch),
			MatcherKind.Editable => (seen.Found && seen.Flag, seen.Found ? (seen.Flag ? "editable" : "not editable") : NoMatch),
			MatcherKind.Attached => (seen.Found, seen.Found ? "in the page" : NoMatch),
			MatcherKind.Empty => (seen.Found && seen.Flag, seen.Found ? (seen.Flag ? "empty" : "not empty") : NoMatch),
			MatcherKind.Count => (seen.Count == m.Number, seen.Count.ToString(CultureInfo.InvariantCulture)),
			MatcherKind.Text => Text(seen, Compare(text, m, o, collapse: true)),
			MatcherKind.Value => Text(seen, Compare(text, m, o with { IgnoreCase = false }, collapse: false)),
			MatcherKind.Class =>
				Text(seen, m.Match == TextMatch.Contains ? HasClasses(text, m.Expected!) : Compare(text, m, o with { IgnoreCase = false }, collapse: false)),
			MatcherKind.Attribute when seen.Found && seen.Text is null => (false, "no such attribute"),
			MatcherKind.Attribute => Text(seen, m.Expected is null || Compare(text, m, o, collapse: false)),
			MatcherKind.Title => (Compare(text, m, o with { IgnoreCase = false }, collapse: true), Quote(text)),
			MatcherKind.Url => (CompareUrl(text, m, o), Quote(text)),
			_ => throw new ArgumentOutOfRangeException(nameof(m), m.Kind, "Unknown matcher."),
		};
	}

	private static (bool Matched, string Received) Text(Observation seen, bool matched) =>
		seen.Found ? (matched, Quote(seen.Text ?? string.Empty)) : (false, NoMatch);

	private static bool Compare(string actual, Matcher m, ExpectOptions o, bool collapse)
	{
		var expected = m.Expected!;
		if (m.Match == TextMatch.Regex)
			return Regex.IsMatch(
				actual,
				expected,
				o.IgnoreCase ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase : RegexOptions.CultureInvariant,
				TimeSpan.FromSeconds(1)
			);
		if (collapse)
		{
			actual = Collapse(actual);
			expected = Collapse(expected);
		}

		var comparison = o.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
		return m.Match == TextMatch.Contains ? actual.Contains(expected, comparison) : string.Equals(actual, expected, comparison);
	}

	private static bool CompareUrl(string actual, Matcher m, ExpectOptions o)
	{
		if (m.Match != TextMatch.Exact)
			return Compare(actual, m, o, collapse: false);
		var glob = Page.UrlGlob(m.Expected!);
		return (o.IgnoreCase ? new Regex(glob.ToString(), glob.Options | RegexOptions.IgnoreCase) : glob).IsMatch(actual);
	}

	private static bool HasClasses(string attribute, string expected)
	{
		var have = attribute.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
		return expected.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).All(have.Contains);
	}

	/// <summary>Whitespace runs become one space, and the ends are trimmed: what Playwright does to a string it compares.</summary>
	internal static string Collapse(string text) => Whitespace().Replace(text, " ").Trim();

	[GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
	private static partial Regex Whitespace();

	private static string Quote(string s)
	{
		var shown = s.Length > MaxReceived ? s[..MaxReceived] + "…" : s;
		return "\"" + shown.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}
}
