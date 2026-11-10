using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class ClickHitTests(ChromeFixture chrome)
{
	private async Task<Page> OpenAsync()
	{
		var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		page.ActionTimeout = TimeSpan.FromSeconds(2);
		await page.GotoAsync(ChromeFixture.Site("cover.html"));
		return page;
	}

	private static Task<string> Log(Page page) => page.Css("#log").TextAsync();

	[Test]
	public async Task AClickOnAnUncoveredButtonLands()
	{
		var page = await OpenAsync();
		await page.Role("button", "Open").ClickAsync();

		(await Log(page)).Should().Be("clicked open");
	}

	[Test]
	public async Task AClickOnACoveredButtonFailsAndNamesWhatCoversIt()
	{
		var page = await OpenAsync();
		var act = () => page.Role("button", "Covered").ClickAsync();

		var failure = await act.Should().ThrowAsync<LocatorTimeoutException>();
		failure.Which.Message.Should().StartWith("Timed out after 2s waiting for role button --name Covered to receive a click at (")
			.And.EndWith("): <div#curtain.cover.modal> would receive it instead.");
		(await Log(page)).Should().Be("nothing", "the click was never sent");
	}

	[Test]
	public async Task AClickWaitsForACoverThatGoesAway()
	{
		var page = await OpenAsync();
		await page.Role("button", "Cleared later").ClickAsync();

		(await Log(page)).Should().Be("clicked later");
	}

	[Test]
	public async Task AClickPassesThroughAnElementWithPointerEventsNone()
	{
		var page = await OpenAsync();
		await page.Role("button", "Through").ClickAsync();

		(await Log(page)).Should().Be("clicked through");
	}

	[Test]
	public async Task AClickOffTheScreenFailsAndSaysNothingIsThere()
	{
		var page = await OpenAsync();
		var act = () => page.Css("#offscreen").ClickAsync();

		var failure = await act.Should().ThrowAsync<LocatorTimeoutException>();
		failure.Which.Message.Should().EndWith("nothing is there: it is outside the viewport or clipped.");
	}

	[Test]
	public async Task ACheckOnAHiddenInputIsReceivedByItsLabel()
	{
		var page = await OpenAsync();
		await page.Css("#toggle").CheckAsync();

		(await Log(page)).Should().Be("toggled true");
	}

	[Test]
	public async Task AClickOnAnElementInsideAButtonIsReceivedByTheElement()
	{
		var page = await OpenAsync();
		await page.Css("#inner").ClickAsync();

		(await Log(page)).Should().Be("clicked inner");
	}

	[Test]
	public async Task HoverAndDragSourceAreHitTestedToo()
	{
		var page = await OpenAsync();

		await ((Func<Task>)(() => page.Role("button", "Covered").HoverAsync())).Should().ThrowAsync<LocatorTimeoutException>();
		await ((Func<Task>)(() => page.Role("button", "Covered").DragToAsync(page.Role("button", "Open")))).Should().ThrowAsync<LocatorTimeoutException>();
	}

	[Test]
	public async Task AJourneyClickOnACoveredButtonFailsTheStepAtTheClick()
	{
		var result = await JourneyRunner.RunAsync(
			Journey.Parse("open cover.html\nfind role button click --name Covered\nget text #log\n", "cover.journey"), chrome.Browser,
			new JourneyOptions { BaseUrl = new Uri(new Uri(ChromeFixture.Site("cover.html")), "./"), ActionTimeout = TimeSpan.FromSeconds(1) });

		result.Failure!.Line.Should().Be(2);
		result.Failure.Message.Should().Contain("<div#curtain.cover.modal> would receive it instead");
		result.Checkpoints.Should().BeEmpty();
	}
}
