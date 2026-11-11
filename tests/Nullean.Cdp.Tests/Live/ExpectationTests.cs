using AwesomeAssertions;

namespace Nullean.Cdp.Tests.Live;

// The assertion engine, called directly: no journeys, no test framework adapter.
[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class ExpectationTests(ChromeFixture chrome)
{
	private static readonly ExpectOptions Quick = new() { Timeout = TimeSpan.FromMilliseconds(200) };

	private async Task<(BrowserContext Context, Page Page)> OpenAsync()
	{
		var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(new Uri(ChromeFixture.Site("assertions.html")).AbsoluteUri);
		return (context, page);
	}

	[Test]
	public async Task ElementStatesHoldWhenTheyAreTrue()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		await page.Css("#go").ExpectAsync(Matcher.Visible);
		await page.Css("#go").ExpectAsync(Matcher.Enabled);
		await page.Css("#off").ExpectAsync(Matcher.Disabled);
		await page.Css("#aria-off").ExpectAsync(Matcher.Disabled);
		await page.Css("#hidden").ExpectAsync(Matcher.Hidden);
		await page.Css("#hidden").ExpectAsync(Matcher.Attached);
		await page.Css("#news").ExpectAsync(Matcher.Checked);
		await page.Css("#name").ExpectAsync(Matcher.Editable);
		await page.Css("#readonly").ExpectAsync(Matcher.Editable, new ExpectOptions { Negate = true });
		await page.Css("#blank").ExpectAsync(Matcher.Empty);
		await page.Css("#empty-div").ExpectAsync(Matcher.Empty);
		await page.Css("#name").ExpectAsync(Matcher.Empty, new ExpectOptions { Negate = true });
	}

	[Test]
	public async Task HiddenPassesForAMissingElementButVisibleDoesNot()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		await page.Css("#nothing-here").ExpectAsync(Matcher.Hidden);
		var result = await page.Css("#nothing-here").CheckAsync(Matcher.Visible, Quick);

		result.Passed.Should().BeFalse();
		result.Message.Should().Be("Timed out after 0.2s waiting for css #nothing-here to be visible: received no element matches.");
	}

	[Test]
	public async Task KeepsCheckingUntilThePageChanges()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		var late = await page.Css("#late").CheckAsync(Matcher.Visible);
		late.Passed.Should().BeTrue();
		late.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(100));

		await page.Css("#enable-later").ExpectAsync(Matcher.Enabled);
		await page.Css("#plain").ExpectAsync(Matcher.Checked);
		await page.Css("#gone").ExpectAsync(Matcher.Visible, new ExpectOptions { Negate = true });
		await page.Css("#gone").ExpectAsync(Matcher.Count(0));
		await page.ExpectAsync(Matcher.Title("Loaded"));
	}

	[Test]
	public async Task OnlyVisibilityAttachmentAndCountsCanBeAssertedAboutNothing()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;
		var missing = page.Css("#nothing");

		// Playwright's rule: these hold for a missing element, negated or not...
		await missing.ExpectAsync(Matcher.Hidden);
		await missing.ExpectAsync(Matcher.Visible, new ExpectOptions { Negate = true });
		await missing.ExpectAsync(Matcher.Attached, new ExpectOptions { Negate = true });
		await missing.ExpectAsync(Matcher.Count(0));
		await missing.ExpectAsync(Matcher.Count(1), new ExpectOptions { Negate = true });

		// ...and every other matcher needs an element, even negated.
		foreach (var matcher in new[]
		{
			Matcher.Enabled,
			Matcher.Disabled,
			Matcher.Checked,
			Matcher.Unchecked,
			Matcher.Focused,
			Matcher.Editable,
			Matcher.Empty,
			Matcher.Text("x"),
			Matcher.Value("x"),
			Matcher.Attribute("x"),
			Matcher.Class("x")
		})
		{
			var negated = await missing.CheckAsync(matcher, Quick with { Negate = true });
			negated.Passed.Should().BeFalse($"--not {matcher.Kind} on nothing");
			negated.Received.Should().Be("no element matches");
		}
	}

	[Test]
	public async Task NegationFailsWhileTheMatcherStillHolds()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		var result = await page.Css("#go").CheckAsync(Matcher.Visible, Quick with { Negate = true });

		result.Passed.Should().BeFalse();
		result.Message.Should().Be("Timed out after 0.2s waiting for css #go to not be visible: received visible.");
	}

	[Test]
	public async Task TextIsWholeAndWhitespaceNormalisedUnlessToldOtherwise()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		await page.Css("#spaced").ExpectAsync(Matcher.Text("one two three"));
		await page.Css("#spaced").ExpectAsync(Matcher.Text("two", TextMatch.Contains));
		await page.Css("#spaced").ExpectAsync(Matcher.Text(@"^\s+one\s+two", TextMatch.Regex));
		await page.Css("#spaced").ExpectAsync(Matcher.Text("ONE TWO THREE"), new ExpectOptions { IgnoreCase = true });
		await page.Css("#count").ExpectAsync(Matcher.Text("2 results"));

		var wrongCase = await page.Css("#count").CheckAsync(Matcher.Text("2 RESULTS"), Quick);
		wrongCase.Passed.Should().BeFalse();
		var partial = await page.Css("#count").CheckAsync(Matcher.Text("2"), Quick);
		partial.Message.Should().Be("Timed out after 0.2s waiting for css #count to have text \"2\": received \"2 results\".");
	}

	[Test]
	public async Task ValueAttributeClassAndCount()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		await page.Css("#name").ExpectAsync(Matcher.Value("Ada"));
		await page.Css("#name").ExpectAsync(Matcher.Value("^A", TextMatch.Regex));
		await page.Css("#name").ExpectAsync(Matcher.Attribute("placeholder"));
		await page.Css("#name").ExpectAsync(Matcher.Attribute("placeholder", "Name"));
		await page.Css("#name").ExpectAsync(Matcher.Attribute("data-state", "RE", TextMatch.Contains), new ExpectOptions { IgnoreCase = true });
		await page.Css("#name").ExpectAsync(Matcher.Attribute("nothing"), new ExpectOptions { Negate = true });
		await page.Css("#go").ExpectAsync(Matcher.Class("primary big"));
		await page.Css("#go").ExpectAsync(Matcher.Class("big primary", TextMatch.Contains));
		await page.Css("#go").ExpectAsync(Matcher.Class("primary"), new ExpectOptions { Negate = true });
		await page.Css("#list li").ExpectAsync(Matcher.Count(3));

		var missing = await page.Css("#name").CheckAsync(Matcher.Attribute("nothing"), Quick);
		missing.Message.Should().Be("Timed out after 0.2s waiting for css #name to have attribute nothing: received no such attribute.");
		var wrongCount = await page.Css("#list li").CheckAsync(Matcher.Count(4), Quick);
		wrongCount.Message.Should().Be("Timed out after 0.2s waiting for css \"#list li\" to have count 4: received 3.");
	}

	[Test]
	public async Task FocusAndLocatorsBeyondCss()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		await page.Css("#name").ExpectAsync(Matcher.Focused, new ExpectOptions { Negate = true });
		await page.Css("#name").FocusAsync();
		await page.Css("#name").ExpectAsync(Matcher.Focused);
		await page.Role("button", "Go").ExpectAsync(Matcher.Visible);
		await page.Css("#list li").Last.ExpectAsync(Matcher.Text("Three"));
		await page.Css("#list li").Nth(1).ExpectAsync(Matcher.Text("Two"));
	}

	[Test]
	public async Task PageTitleAndUrl()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		await page.ExpectAsync(Matcher.Title("Load", TextMatch.Contains));
		await page.ExpectAsync(Matcher.Url("**/assertions.html"));
		await page.ExpectAsync(Matcher.Url("**/ASSERTIONS.HTML"), new ExpectOptions { IgnoreCase = true });
		await page.ExpectAsync(Matcher.Url(@"assertions\.html$", TextMatch.Regex));
		await page.ExpectAsync(Matcher.Url("**/about.html"), new ExpectOptions { Negate = true });

		var result = await page.CheckAsync(Matcher.Title("Other"), Quick);
		result.Message.Should().Be("Timed out after 0.2s waiting for the page title to have title \"Other\": received \"Loaded\".");
	}

	[Test]
	public async Task ExpectAsyncThrowsWithTheResult()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		var thrown = await FluentActions.Awaiting(() => page.Css("#count").ExpectAsync(Matcher.Text("3"), Quick)).Should().ThrowAsync<ExpectationException>();

		thrown.Which.Result.Passed.Should().BeFalse();
		thrown.Which.Result.Received.Should().Be("\"2 results\"");
		thrown.Which.Message.Should().Be("Timed out after 0.2s waiting for css #count to have text \"3\": received \"2 results\".");
		thrown.Which.Should().BeAssignableTo<TimeoutException>();
	}

	[Test]
	public async Task ABadRegularExpressionIsRejectedBeforeAnyWaiting()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		await FluentActions
			.Awaiting(() => page.Css("#count").CheckAsync(Matcher.Text("(", TextMatch.Regex), Quick))
			.Should()
			.ThrowAsync<ArgumentException>()
			.WithMessage("'(' is not a valid regular expression*");
	}

	[Test]
	public async Task PageAndElementMatchersAreKeptApart()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		await FluentActions
			.Awaiting(() => page.Css("#go").CheckAsync(Matcher.Title("x")))
			.Should()
			.ThrowAsync<ArgumentException>()
			.WithMessage("*use Page.CheckAsync*");
		await FluentActions.Awaiting(() => page.CheckAsync(Matcher.Visible)).Should().ThrowAsync<ArgumentException>().WithMessage("*use Locator.CheckAsync*");
	}

	[Test]
	public void MatchersDescribeThemselvesForMessages()
	{
		Matcher.Visible.Describe().Should().Be("be visible");
		Matcher.Visible.Describe(negate: true).Should().Be("not be visible");
		Matcher.Text("a \"b\"").Describe().Should().Be("have text \"a \\\"b\\\"\"");
		Matcher.Text("a", TextMatch.Contains).Describe().Should().Be("contain text \"a\"");
		Matcher.Text("a", TextMatch.Regex).Describe().Should().Be("match text \"a\"");
		Matcher.Class("a b", TextMatch.Contains).Describe().Should().Be("contain the classes \"a b\"");
		Matcher.Url("**/x").Describe().Should().Be("have a URL matching \"**/x\"");
		Matcher.Count(2).Describe().Should().Be("have count 2");
	}
}
