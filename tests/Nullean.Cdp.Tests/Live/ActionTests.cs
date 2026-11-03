using AwesomeAssertions;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class ActionTests(ChromeFixture chrome)
{
	private async Task<(BrowserContext Context, Page Page)> Open(string page = "actions.html")
	{
		var context = await chrome.Browser.NewContextAsync();
		var tab = await context.NewPageAsync();
		await tab.GotoAsync(ChromeFixture.Site(page));
		return (context, tab);
	}

	private static Task<string> Log(Page page) => page.Css("#log").TextAsync();

	[Test]
	public async Task TheMainFramesIdIsTheTargetId()
	{
		var (context, page) = await Open();
		await using var _ = context;

		var tree = await page.SendAsync("Page.getFrameTree");
		tree.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString().Should().Be(page.TargetId);
	}

	[Test]
	public async Task ClicksByRoleAndName()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.Role("button", "Say hello").ClickAsync();
		(await Log(page)).Should().Be("clicked hello");
	}

	[Test]
	public async Task WaitsForAnElementThatAppearsLate()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.Role("button", "Appears late", exact: true).ClickAsync();
		(await Log(page)).Should().Be("clicked late");
	}

	[Test]
	public async Task WaitsForAButtonToBeEnabled()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.TestId("later-enabled").ClickAsync();
		(await Log(page)).Should().Be("clicked later");
	}

	[Test]
	public async Task AHiddenMatchTimesOutAndSaysWhy()
	{
		var (context, page) = await Open();
		await using var _ = context;
		page.ActionTimeout = TimeSpan.FromMilliseconds(300);

		var act = () => page.Text("Hidden button").ClickAsync();
		await act.Should().ThrowAsync<LocatorTimeoutException>()
			.WithMessage("Timed out after 0.3s waiting for text \"Hidden button\" to be visible and enabled: 1 element matches, none visible.");
	}

	[Test]
	public async Task ADisabledMatchTimesOutAndSaysWhy()
	{
		var (context, page) = await Open();
		await using var _ = context;
		page.ActionTimeout = TimeSpan.FromMilliseconds(300);

		var act = () => page.Role("button", "Never enabled").ClickAsync();
		await act.Should().ThrowAsync<LocatorTimeoutException>()
			.WithMessage("Timed out after 0.3s waiting for role button --name \"Never enabled\" to be visible and enabled: 1 element matches, the visible one is disabled.");
		(await Log(page)).Should().Be("nothing yet");
	}

	[Test]
	public async Task GoingBackWithinTheDocumentSettles()
	{
		var (context, page) = await Open();
		await using var _ = context;
		page.NavigationTimeout = TimeSpan.FromSeconds(2);

		await page.Text("Jump to bottom").ClickAsync();
		await page.BackAsync();

		(await page.UrlAsync()).Should().EndWith("/actions.html");
	}

	[Test]
	public async Task NoMatchTimesOutAndSaysSo()
	{
		var (context, page) = await Open();
		await using var _ = context;
		page.ActionTimeout = TimeSpan.FromMilliseconds(200);

		var act = () => page.Css("#nope").ClickAsync();
		await act.Should().ThrowAsync<LocatorTimeoutException>()
			.WithMessage("Timed out after 0.2s waiting for css #nope to be visible and enabled: no element matches.");
	}

	[Test]
	public async Task AnInvalidSelectorIsAnErrorNotATimeout()
	{
		var (context, page) = await Open();
		await using var _ = context;

		var act = () => page.Css("button[").ClickAsync();
		await act.Should().ThrowAsync<LocatorException>().WithMessage("Cannot look up css \"button[\": SyntaxError*");
	}

	[Test]
	public async Task HoversByTitleAndClicksByAlt()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.Title("Tooltip").HoverAsync();
		(await Log(page)).Should().Be("hovered");
		await page.Alt("Pixel").ClickAsync();
		(await Log(page)).Should().Be("clicked pixel");
	}

	[Test]
	public async Task FillsByLabelAndPlaceholder()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.Label("Your name").FillAsync("Grace Hopper");
		(await page.Label("Your name").ValueAsync()).Should().Be("Grace Hopper");
		(await Log(page)).Should().Be("typed Grace Hopper");

		await page.Placeholder("ada lovelace").FillAsync("");
		(await page.Placeholder("Ada Lovelace", exact: true).ValueAsync()).Should().BeEmpty();
	}

	[Test]
	public async Task FillingAButtonIsAnError()
	{
		var (context, page) = await Open();
		await using var _ = context;

		var act = () => page.Role("button", "Say hello").FillAsync("x");
		await act.Should().ThrowAsync<LocatorException>()
			.WithMessage("Cannot fill role button --name \"Say hello\": the element is not an input, text area or editable element.");
	}

	[Test]
	public async Task TypesAndPressesKeys()
	{
		var (context, page) = await Open();
		await using var _ = context;
		var notes = page.Label("Notes");

		await notes.TypeAsync("hi!");
		(await notes.ValueAsync()).Should().Be("hi!");
		(await Log(page)).Should().Be("key !");

		await notes.PressAsync("Shift+b");
		(await Log(page)).Should().Be("key B shift");
		(await notes.ValueAsync()).Should().Be("hi!B");

		await notes.PressAsync("Control+a");
		(await Log(page)).Should().Be("key a ctrl");
		(await notes.ValueAsync()).Should().Be("hi!B", "a shortcut types nothing");

		await page.PressAsync("Backspace");
		(await notes.ValueAsync()).Should().Be("hi!");
	}

	[Test]
	public async Task UnknownKeysAndNonModifierChordsAreRejected()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await FluentActions.Awaiting(() => page.PressAsync("Bogus")).Should().ThrowAsync<ArgumentException>().WithMessage("Unknown key 'Bogus'*");
		await FluentActions.Awaiting(() => page.PressAsync("a+b")).Should().ThrowAsync<ArgumentException>().WithMessage("'a' in 'a+b' is not a modifier*");
	}

	[Test]
	public async Task ChecksAndUnchecks()
	{
		var (context, page) = await Open();
		await using var _ = context;
		var box = page.Label("Newsletter");

		await box.CheckAsync();
		(await box.IsCheckedAsync()).Should().BeTrue();
		(await Log(page)).Should().Be("news true");

		await page.EvaluateAsync("document.getElementById('log').textContent = 'reset'");
		await box.CheckAsync();
		(await Log(page)).Should().Be("reset", "checking a checked box does not click it");

		await box.UncheckAsync();
		(await box.IsCheckedAsync()).Should().BeFalse();
	}

	[Test]
	public async Task SelectsByLabelOrValue()
	{
		var (context, page) = await Open();
		await using var _ = context;
		var colour = page.Label("Colour");

		(await colour.SelectAsync(["Green"])).Should().Equal("g");
		(await colour.ValueAsync()).Should().Be("g");
		(await Log(page)).Should().Be("colour g");
		(await colour.SelectAsync(["b"])).Should().Equal("b");

		var act = () => colour.SelectAsync(["Purple"]);
		await act.Should().ThrowAsync<LocatorException>().WithMessage("Cannot select in label Colour: it has no option \"Purple\".");
		var many = () => colour.SelectAsync(["r", "g"]);
		await many.Should().ThrowAsync<LocatorException>().WithMessage("*does not allow several options.");
	}

	[Test]
	public async Task AClickThatNavigatesWaitsForTheNewPageAndHistoryWorks()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.Role("link", "Go to about").ClickAsync();
		(await page.TitleAsync()).Should().Be("About");
		(await page.EvaluateAsync("document.readyState")).GetString().Should().Be("complete");

		await page.BackAsync();
		(await page.TitleAsync()).Should().Be("Actions");
		await page.ForwardAsync();
		(await page.TitleAsync()).Should().Be("About");
		await page.ForwardAsync(); // at the end: nothing happens
		(await page.TitleAsync()).Should().Be("About");
	}

	[Test]
	public async Task AClickWaitsForTheNextPageToLoad()
	{
		_ = chrome.Browser; // skips without a Chrome
		await using var server = new SlowServer(TimeSpan.FromMilliseconds(800));
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(server.Url);

		await page.Role("link", "Next page").ClickAsync();

		(await page.EvaluateAsync("document.readyState")).GetString().Should().Be("complete");
		(await page.TitleAsync()).Should().Be("Next");
	}

	[Test]
	public async Task ALinkWithinTheDocumentReturnsAtOnce()
	{
		var (context, page) = await Open();
		await using var _ = context;

		// Waiting for a load that never comes would fail with "did not finish loading" after this.
		page.NavigationTimeout = TimeSpan.FromSeconds(2);
		await page.Text("Jump to bottom").ClickAsync();
		(await page.UrlAsync()).Should().EndWith("#bottom");
	}

	[Test]
	public async Task AnActionThatStartsNoNavigationDoesNotWait()
	{
		var (context, page) = await Open();
		await using var _ = context;

		// The form's submit handler prevents navigation; waiting for one would fail after the navigation timeout.
		page.NavigationTimeout = TimeSpan.FromSeconds(2);
		await page.Label("Your name").PressAsync("Enter");
		(await Log(page)).Should().Be("submitted prefilled");
	}

	[Test]
	public async Task ScrollsBeforeClicking()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.Role("button", "Far below").ClickAsync();
		(await Log(page)).Should().Be("clicked offscreen");
	}

	[Test]
	public async Task TextMatchingIsLooseUnlessExact()
	{
		var (context, page) = await Open();
		await using var _ = context;

		(await page.Text("  say   HELLO ").CountAsync()).Should().Be(1);
		(await page.Text("say hello", exact: true).CountAsync()).Should().Be(0);
		(await page.Text("Say hello", exact: true).TextAsync()).Should().Be("Say hello");
		(await page.Role("button", "say").CountAsync()).Should().Be(1);
		await page.Role("button", "Appears late").WaitAsync();
		(await page.Role("button").CountAsync()).Should().Be(6, "the display:none button is not in the accessibility tree");
	}

	[Test]
	public async Task ReadsAndStates()
	{
		var (context, page) = await Open();
		await using var _ = context;

		(await page.Css("form label").HtmlAsync()).Should().Be("Your name");
		(await page.Css("#name").AttributeAsync("placeholder")).Should().Be("Ada Lovelace");
		(await page.Css("#name").AttributeAsync("missing")).Should().BeNull();
		(await page.Text("Hidden button").IsVisibleAsync()).Should().BeFalse();
		(await page.Css("#nope").IsVisibleAsync()).Should().BeFalse();
		(await page.Role("button", "Say hello").IsEnabledAsync()).Should().BeTrue();
	}

	[Test]
	public async Task WaitsForVisible()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.Css("[data-testid=later-enabled]:not([disabled])").WaitAsync();
		(await page.TestId("later-enabled").IsEnabledAsync()).Should().BeTrue();
	}

	[Test]
	public async Task ReloadsTheDocument()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.Role("button", "Say hello").ClickAsync();
		await page.ReloadAsync();
		(await Log(page)).Should().Be("nothing yet");
	}

	[Test]
	public async Task EmulatesViewportAndColourScheme()
	{
		var (context, page) = await Open();
		await using var _ = context;

		await page.SetViewportAsync(400, 300);
		await page.SetColorSchemeAsync("dark");

		(await page.EvaluateAsync("innerWidth")).GetInt32().Should().Be(400);
		(await page.EvaluateAsync("matchMedia('(prefers-color-scheme: dark)').matches")).GetBoolean().Should().BeTrue();
		await FluentActions.Awaiting(() => page.SetColorSchemeAsync("purple")).Should().ThrowAsync<ArgumentException>();
	}

	[Test]
	public async Task LocatorsOnOnePageRunConcurrently()
	{
		var (context, page) = await Open();
		await using var _ = context;

		var reads = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => i % 2 == 0
			? page.Role("button", "Say hello").TextAsync()
			: page.Label("Your name").ValueAsync()));

		reads.Where((_, i) => i % 2 == 0).Should().AllBe("Say hello");
		reads.Where((_, i) => i % 2 == 1).Should().AllBe("prefilled");
	}
}
