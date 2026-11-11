using AwesomeAssertions;
using Nullean.Cdp.Accessibility;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class BrowserTests(ChromeFixture chrome)
{
	private async Task<(BrowserContext Context, Page Page)> Open(string page)
	{
		var context = await chrome.Browser.NewContextAsync();
		var tab = await context.NewPageAsync();
		await tab.GotoAsync(ChromeFixture.Site(page));
		return (context, tab);
	}

	[Test]
	public async Task ReportsItsVersion() => (await chrome.Browser.VersionAsync()).Should().MatchRegex(@"^(Headless)?Chrom(e|ium)/\d+\.");

	[Test]
	public async Task NavigatesAndReadsUrlAndTitle()
	{
		var (context, page) = await Open("index.html");
		await using var _ = context;

		(await page.TitleAsync()).Should().Be("Fixture shop");
		(await page.UrlAsync()).Should().EndWith("/site/index.html");
	}

	[Test]
	public async Task SnapshotOfALivePage()
	{
		var (context, page) = await Open("index.html");
		await using var _ = context;

		var snapshot = await page.SnapshotAsync(new AxSnapshotOptions { Compact = true, Selector = "form" });

		// Asserted by line, not as a whole, so a new Chrome that adds detail elsewhere does not fail it.
		snapshot
			.Split('\n')
			.Should()
			.Contain(["- form", "  - textbox \"Search\"", "  - checkbox \"Only in stock\" [checked=true]", "  - button \"Disabled\" [disabled]",]);
		snapshot.Should().NotContain("ref=").And.NotContain("Not shown");
	}

	[Test]
	public async Task ASelectorThatMatchesNothingSaysSo()
	{
		var (context, page) = await Open("index.html");
		await using var _ = context;

		var act = () => page.SnapshotAsync(new AxSnapshotOptions { Selector = "#missing" });
		await act.Should().ThrowAsync<PageSelectorException>().WithMessage("Selector '#missing' did not match any element");
	}

	[Test]
	public async Task GotoWaitsForTheLoadEvent()
	{
		_ = chrome.Browser; // skips without a Chrome
		await using var server = new SlowServer(TimeSpan.FromMilliseconds(800));
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();

		await page.GotoAsync(server.Url);

		(await page.EvaluateAsync("document.readyState")).GetString().Should().Be("complete");
		(await page.EvaluateAsync("document.images[0].complete && document.images[0].naturalWidth")).GetInt32().Should().Be(1);
	}

	[Test]
	public async Task AFailedNavigationThrowsWithTheBrowsersError()
	{
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();

		var act = () => page.GotoAsync(ChromeFixture.Site("missing.html"));
		(await act.Should().ThrowAsync<PageNavigationException>()).Which.Error.Should().Be("net::ERR_FILE_NOT_FOUND");
	}

	[Test]
	public async Task AFragmentNavigationReturnsWithoutALoadEvent()
	{
		var (context, page) = await Open("index.html");
		await using var _ = context;

		await page.GotoAsync(ChromeFixture.Site("index.html") + "#section").WaitAsync(TimeSpan.FromSeconds(5));
		(await page.UrlAsync()).Should().EndWith("#section");
	}

	[Test]
	public async Task ScriptErrorsThrow()
	{
		var (context, page) = await Open("index.html");
		await using var _ = context;

		var act = () => page.EvaluateAsync("nope()");
		await act.Should().ThrowAsync<PageScriptException>().WithMessage("ReferenceError: nope is not defined*");
		(await page.EvaluateAsync("Promise.resolve(40 + 2)")).GetInt32().Should().Be(42);
	}

	[Test]
	public async Task ContextsDoNotShareStorage()
	{
		var (a, pageA) = await Open("index.html");
		var (b, pageB) = await Open("index.html");
		await using var _ = a;
		await using var __ = b;

		await pageA.EvaluateAsync("localStorage.setItem('k', 'a')");

		(await pageA.EvaluateAsync("localStorage.getItem('k')")).GetString().Should().Be("a");
		(await pageB.EvaluateAsync("localStorage.getItem('k')")).ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
	}

	[Test]
	public async Task ParallelContextsEachSeeTheirOwnPage()
	{
		var pages = new[] { "index.html", "about.html", "index.html", "about.html", "index.html", "about.html" };
		var titles = await Task.WhenAll(pages.Select(async p =>
		{
			var (context, page) = await Open(p);
			await using var _ = context;
			var snapshot = await page.SnapshotAsync(new AxSnapshotOptions { Interactive = true });
			return (await page.TitleAsync(), snapshot);
		}));

		titles.Select(t => t.Item1).Should().Equal("Fixture shop", "About", "Fixture shop", "About", "Fixture shop", "About");
		titles.Where((_, i) => i % 2 == 1).Should().AllSatisfy(t => t.snapshot.Should().Contain("link \"Back home\""));
	}

	[Test]
	public async Task DisposingStopsChromeAndRemovesItsProfile()
	{
		_ = chrome.Browser; // skips without a Chrome
		var browser = await Cdp.Browser.LaunchAsync();
		var context = await browser.NewContextAsync();
		await context.NewPageAsync();
		var profile = browser.UserDataDirectory;
		Directory.Exists(profile).Should().BeTrue();

		await browser.DisposeAsync();

		browser.Connection.IsClosed.Should().BeTrue();
		Directory.Exists(profile).Should().BeFalse();
		await context.DisposeAsync();
	}
}
