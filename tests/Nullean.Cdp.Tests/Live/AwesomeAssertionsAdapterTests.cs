using AwesomeAssertions;
using AwesomeAssertions.Execution;

namespace Nullean.Cdp.Tests.Live;

// The engine through AwesomeAssertions: `because`, AssertionScope and the message all behave as people expect.
[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class AwesomeAssertionsAdapterTests(ChromeFixture chrome)
{
	private static readonly ExpectOptions Quick = new() { Timeout = TimeSpan.FromMilliseconds(200) };

	private async Task<(BrowserContext Context, Page Page)> OpenAsync()
	{
		var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(ChromeFixture.Site("assertions.html"));
		return (context, page);
	}

	[Test]
	public async Task PassingAssertionsChain()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		// `And` chains a second assertion on the same locator.
		var first = await page.Css("#count").Should().HaveTextAsync("2 results");
		await first.And.BeVisibleAsync();
		await page.Css("#go").Should().BeVisibleAsync();
		await page.Css("#late").Should().BeVisibleAsync();
		await page.Css("#nothing").Should().BeHiddenAsync();
		await page.Css("#list li").Should().HaveCountAsync(3);
		await page.Should().HaveTitleAsync("Loaded");
	}

	[Test]
	public async Task AFailureCarriesTheEnginesMessageAndTheReason()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		var act = () => page.Css("#count").Should().HaveTextAsync("3", Quick, "the basket was filled");

		var thrown = await act.Should().ThrowAsync<Exception>();
		thrown.Which.Message.Should().Contain("Timed out after 0.2s waiting for css #count to have text \"3\": received \"2 results\".")
			.And.Contain("because the basket was filled");
	}

	[Test]
	public async Task SeveralFailuresInOneScopeAreReportedTogether()
	{
		var (context, page) = await OpenAsync();
		await using var _ = context;

		var act = async () =>
		{
			using var scope = new AssertionScope();
			await page.Css("#count").Should().HaveTextAsync("3", Quick);
			await page.Css("#go").Should().BeHiddenAsync(Quick);
			await page.Should().HaveTitleAsync("Other", Quick);
		};

		var thrown = await act.Should().ThrowAsync<Exception>();
		thrown.Which.Message.Should().Contain("css #count to have text").And.Contain("css #go to be hidden").And.Contain("the page title to have title");
	}
}
