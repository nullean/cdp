using System.Globalization;

namespace Nullean.Cdp;

/// <summary>What an assertion checks, after Playwright's web-first assertions.</summary>
public enum MatcherKind
{
	/// <summary>Attached and visible (<c>toBeVisible</c>).</summary>
	Visible,

	/// <summary>Not visible, or no match at all (<c>toBeHidden</c>).</summary>
	Hidden,

	/// <summary>A match that is not disabled (<c>toBeEnabled</c>).</summary>
	Enabled,

	/// <summary>A match that is disabled (<c>toBeDisabled</c>).</summary>
	Disabled,

	/// <summary>A checked checkbox, radio or <c>aria-checked</c> element (<c>toBeChecked</c>).</summary>
	Checked,

	/// <summary>A match that is not checked.</summary>
	Unchecked,

	/// <summary>The element with focus (<c>toBeFocused</c>).</summary>
	Focused,

	/// <summary>An enabled, not read-only input, textarea, select or editable element (<c>toBeEditable</c>).</summary>
	Editable,

	/// <summary>A match in the document, visible or not (<c>toBeAttached</c>).</summary>
	Attached,

	/// <summary>An input with no value, or an element with no text (<c>toBeEmpty</c>).</summary>
	Empty,

	/// <summary>The element's text (<c>toHaveText</c>, <c>toContainText</c>).</summary>
	Text,

	/// <summary>An input's value (<c>toHaveValue</c>).</summary>
	Value,

	/// <summary>An attribute's presence, or its value (<c>toHaveAttribute</c>).</summary>
	Attribute,

	/// <summary>The <c>class</c> attribute, or the classes it holds (<c>toHaveClass</c>, <c>toContainClass</c>).</summary>
	Class,

	/// <summary>How many elements match (<c>toHaveCount</c>).</summary>
	Count,

	/// <summary>The page's title, on a <see cref="Page"/> (<c>toHaveTitle</c>).</summary>
	Title,

	/// <summary>The page's URL, on a <see cref="Page"/> (<c>toHaveURL</c>).</summary>
	Url,
}

/// <summary>How an expected text is compared with what the page has.</summary>
public enum TextMatch
{
	/// <summary>The whole text. Text is compared with whitespace collapsed; a URL is a glob (<see cref="Page.UrlGlob"/>).</summary>
	Exact,

	/// <summary>The expected text is somewhere in it; for <see cref="MatcherKind.Class"/>, every expected class is present.</summary>
	Contains,

	/// <summary>The expected text is a regular expression the whole value is searched with, as written (no flags).</summary>
	Regex,
}

/// <summary>
/// What an assertion checks, as data: the same value is built by a journey's <c>expect</c> line, by code, and by any
/// test framework adapter, which reports the <see cref="ExpectationResult"/> its own way.
/// </summary>
/// <param name="Kind">What is checked.</param>
/// <param name="Expected">The expected text, regular expression, attribute value or classes; <c>null</c> where the kind takes none.</param>
/// <param name="Name">The attribute's name for <see cref="MatcherKind.Attribute"/>.</param>
/// <param name="Match">How <paramref name="Expected"/> is compared.</param>
/// <param name="Number">The expected count for <see cref="MatcherKind.Count"/>.</param>
public sealed record Matcher(MatcherKind Kind, string? Expected = null, string? Name = null, TextMatch Match = TextMatch.Exact, int? Number = null)
{
	/// <summary>Attached and visible.</summary>
	public static Matcher Visible { get; } = new(MatcherKind.Visible);

	/// <summary>Not visible, or no match.</summary>
	public static Matcher Hidden { get; } = new(MatcherKind.Hidden);

	/// <summary>Not disabled.</summary>
	public static Matcher Enabled { get; } = new(MatcherKind.Enabled);

	/// <summary>Disabled.</summary>
	public static Matcher Disabled { get; } = new(MatcherKind.Disabled);

	/// <summary>Checked.</summary>
	public static Matcher Checked { get; } = new(MatcherKind.Checked);

	/// <summary>A match that is not checked.</summary>
	public static Matcher Unchecked { get; } = new(MatcherKind.Unchecked);

	/// <summary>Has focus.</summary>
	public static Matcher Focused { get; } = new(MatcherKind.Focused);

	/// <summary>Takes input.</summary>
	public static Matcher Editable { get; } = new(MatcherKind.Editable);

	/// <summary>In the document.</summary>
	public static Matcher Attached { get; } = new(MatcherKind.Attached);

	/// <summary>No value, or no text.</summary>
	public static Matcher Empty { get; } = new(MatcherKind.Empty);

	/// <summary>The element's text.</summary>
	public static Matcher Text(string expected, TextMatch match = TextMatch.Exact) => new(MatcherKind.Text, Required(expected), Match: match);

	/// <summary>An input's value, compared as it is.</summary>
	public static Matcher Value(string expected, TextMatch match = TextMatch.Exact) => new(MatcherKind.Value, Required(expected), Match: match);

	/// <summary>An attribute: present when <paramref name="expected"/> is <c>null</c>, else its value.</summary>
	public static Matcher Attribute(string name, string? expected = null, TextMatch match = TextMatch.Exact) =>
		new(MatcherKind.Attribute, expected, Required(name), match);

	/// <summary>The <c>class</c> attribute: the whole value, or with <see cref="TextMatch.Contains"/> every class listed.</summary>
	public static Matcher Class(string expected, TextMatch match = TextMatch.Exact) => new(MatcherKind.Class, Required(expected), Match: match);

	/// <summary>Exactly <paramref name="count"/> matches; none is zero.</summary>
	public static Matcher Count(int count) =>
		count >= 0 ? new(MatcherKind.Count, Number: count) : throw new ArgumentOutOfRangeException(nameof(count), "A count is zero or more.");

	/// <summary>The page's title.</summary>
	public static Matcher Title(string expected, TextMatch match = TextMatch.Exact) => new(MatcherKind.Title, Required(expected), Match: match);

	/// <summary>The page's URL: a glob where <c>**</c> is anything and <c>*</c> anything but <c>/</c>, or a regular expression.</summary>
	public static Matcher Url(string expected, TextMatch match = TextMatch.Exact) => new(MatcherKind.Url, Required(expected), Match: match);

	/// <summary>Whether this matcher is about the page, not an element.</summary>
	public bool IsPageLevel => Kind is MatcherKind.Title or MatcherKind.Url;

	/// <summary>The expectation as it reads in a message: <c>be visible</c>, <c>have text "3"</c>.</summary>
	public string Describe(bool negate = false)
	{
		var phrase = Kind switch
		{
			MatcherKind.Visible => "be visible",
			MatcherKind.Hidden => "be hidden",
			MatcherKind.Enabled => "be enabled",
			MatcherKind.Disabled => "be disabled",
			MatcherKind.Checked => "be checked",
			MatcherKind.Unchecked => "be unchecked",
			MatcherKind.Focused => "be focused",
			MatcherKind.Editable => "be editable",
			MatcherKind.Attached => "be in the page",
			MatcherKind.Empty => "be empty",
			MatcherKind.Text => $"{Verb("have", "contain", "match")} text {Quote(Expected!)}",
			MatcherKind.Value => $"{Verb("have", "contain", "match")} value {Quote(Expected!)}",
			MatcherKind.Attribute when Expected is null => $"have attribute {Name}",
			MatcherKind.Attribute => $"{Verb("have", "contain", "match")} attribute {Name} {Quote(Expected)}",
			MatcherKind.Class =>
				Match == TextMatch.Contains ? $"contain the classes {Quote(Expected!)}" : $"{Verb("have", "contain", "match")} class {Quote(Expected!)}",
			MatcherKind.Count => $"have count {Number!.Value.ToString(CultureInfo.InvariantCulture)}",
			MatcherKind.Title => $"{Verb("have", "contain", "match")} title {Quote(Expected!)}",
			MatcherKind.Url =>
				Match == TextMatch.Exact ? $"have a URL matching {Quote(Expected!)}" : $"{Verb("have", "contain", "match")} URL {Quote(Expected!)}",
			_ => Kind.ToString(),
		};
		if (!negate)
			return phrase;
		// "be visible" -> "not be visible", "have text" -> "not have text".
		return "not " + phrase;

		string Verb(string exact, string contains, string regex) => Match switch
		{
			TextMatch.Contains => contains,
			TextMatch.Regex => regex,
			_ => exact,
		};

		static string Quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}

	private static string Required(string value) => value ?? throw new ArgumentNullException(nameof(value));
}

/// <summary>How an assertion is run.</summary>
public sealed record ExpectOptions
{
	/// <summary>How long to keep checking before giving up; the page's <see cref="Page.ActionTimeout"/> when <c>null</c>.</summary>
	public TimeSpan? Timeout { get; init; }

	/// <summary>Pass when the matcher does not hold (Playwright's <c>.not</c>). It keeps checking until it does not hold or time runs out.</summary>
	public bool Negate { get; init; }

	/// <summary>Compare text, attribute values and URLs without regard to case.</summary>
	public bool IgnoreCase { get; init; }

	/// <summary>Read an element's text as <c>innerText</c> (what is rendered) instead of <c>textContent</c>.</summary>
	public bool UseInnerText { get; init; }

	/// <summary>The pause between checks.</summary>
	public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// The outcome of an assertion: what framework adapters report. <see cref="Message"/> is plain text that names what was
/// checked and what the last check saw.
/// </summary>
/// <param name="Passed">Whether the matcher held (or, with <see cref="ExpectOptions.Negate"/>, did not) before time ran out.</param>
/// <param name="Subject">What was checked: <c>role button --name "Go"</c>, <c>the page title</c>.</param>
/// <param name="Expectation">What it should do: <c>be visible</c>, <c>not have text "3"</c>.</param>
/// <param name="Received">What the last check saw, or <c>null</c> when the assertion passed.</param>
/// <param name="Timeout">How long it was allowed to take.</param>
/// <param name="Elapsed">How long it took.</param>
public sealed record ExpectationResult(bool Passed, string Subject, string Expectation, string? Received, TimeSpan Timeout, TimeSpan Elapsed)
{
	/// <summary>Empty when it passed; else <c>Timed out after 5s waiting for #count to have text "3": received "2".</c></summary>
	public string Message =>
		Passed
			? string.Empty
			: $"Timed out after {Timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s waiting for {Subject} to {Expectation}: received {Received}.";
}

/// <summary>An assertion did not hold within its timeout. It is a <see cref="TimeoutException"/>, so a journey fails at the step.</summary>
public sealed class ExpectationException : TimeoutException
{
	/// <summary>Wraps a failed <paramref name="result"/>; the message is <see cref="ExpectationResult.Message"/>.</summary>
	public ExpectationException(ExpectationResult result) : base(result?.Message ?? throw new ArgumentNullException(nameof(result)))
	{
		Result = result;
	}

	/// <summary>What was checked and what the last check saw.</summary>
	public ExpectationResult Result { get; }
}
