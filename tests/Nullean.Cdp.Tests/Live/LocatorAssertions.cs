using AwesomeAssertions;
using AwesomeAssertions.Execution;
using AwesomeAssertions.Formatting;
using AwesomeAssertions.Primitives;

namespace Nullean.Cdp.Tests.Live;

// Proof, not product (the engine's message goes through the assertion chain verbatim, with the `because` reason): a few `Should()` assertions over Nullean.Cdp's framework-free engine. A real adapter (a package of
// its own, or part of Nullean.Snapshots.Browser.Assertions) would add the rest of the matchers the same way: build a
// Matcher, call CheckAsync, report the result's plain-text message through the assertion chain.
// Reports an ExpectationResult the way an AwesomeAssertions assertion does. The message is passed as an argument, never as
// the format string (a selector or page text can hold {braces}), and the format string carries {reason}: AwesomeAssertions
// adds a `because` reason only where the format has it. Nullean.Snapshots' SnapshotAssertions.FailUnless does the first
// but not the second, so it cannot carry a reason yet.
internal static class Reporting
{
	static Reporting() => AssertionConfiguration.Current.Formatting.AddFormatter(new VerbatimFormatter());

	public static void Report(AssertionChain chain, ExpectationResult result, string because, object[] becauseArgs) =>
		chain.BecauseOf(because, becauseArgs).ForCondition(result.Passed).FailWith("{0}{reason}", new Verbatim(result.Message));

	private sealed record Verbatim(string Text);

	private sealed class VerbatimFormatter : IValueFormatter
	{
		public bool CanHandle(object value) => value is Verbatim;

		public void Format(object value, FormattedObjectGraph formattedGraph, FormattingContext context, FormatChild formatChild) =>
			formattedGraph.AddFragment(((Verbatim)value).Text);
	}
}

public static class CdpAssertionExtensions
{
	public static LocatorAssertions Should(this Locator subject) => new(subject, AssertionChain.GetOrCreate());

	public static PageAssertions Should(this Page subject) => new(subject, AssertionChain.GetOrCreate());
}

public class LocatorAssertions(Locator subject, AssertionChain chain) : ReferenceTypeAssertions<Locator, LocatorAssertions>(subject, chain)
{
	protected override string Identifier => "locator";

	public Task<AndConstraint<LocatorAssertions>> BeVisibleAsync(ExpectOptions? options = null, string because = "", params object[] becauseArgs) =>
		CheckAsync(Matcher.Visible, options, because, becauseArgs);

	public Task<AndConstraint<LocatorAssertions>> BeHiddenAsync(ExpectOptions? options = null, string because = "", params object[] becauseArgs) =>
		CheckAsync(Matcher.Hidden, options, because, becauseArgs);

	public Task<AndConstraint<LocatorAssertions>> HaveTextAsync(
		string expected,
		ExpectOptions? options = null,
		string because = "",
		params object[] becauseArgs
	) => CheckAsync(Matcher.Text(expected), options, because, becauseArgs);

	public Task<AndConstraint<LocatorAssertions>> HaveCountAsync(int count, ExpectOptions? options = null, string because = "", params object[] becauseArgs) =>
		CheckAsync(Matcher.Count(count), options, because, becauseArgs);

	private async Task<AndConstraint<LocatorAssertions>> CheckAsync(Matcher matcher, ExpectOptions? options, string because, object[] becauseArgs)
	{
		var result = await Subject.CheckAsync(matcher, options);
		Reporting.Report(CurrentAssertionChain, result, because, becauseArgs);
		return new AndConstraint<LocatorAssertions>(this);
	}
}

public class PageAssertions(Page subject, AssertionChain chain) : ReferenceTypeAssertions<Page, PageAssertions>(subject, chain)
{
	protected override string Identifier => "page";

	public async Task<AndConstraint<PageAssertions>> HaveTitleAsync(
		string expected,
		ExpectOptions? options = null,
		string because = "",
		params object[] becauseArgs
	)
	{
		var result = await Subject.CheckAsync(Matcher.Title(expected), options);
		Reporting.Report(CurrentAssertionChain, result, because, becauseArgs);
		return new AndConstraint<PageAssertions>(this);
	}
}
