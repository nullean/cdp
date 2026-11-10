using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class MoreCommandTests(ChromeFixture chrome)
{
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	[Test]
	public async Task RunsTheParityJourneyAndReadsWhatAgentBrowserRead()
	{
		var journey = Journey.Load(Path.Combine(AppContext.BaseDirectory, "site", "journeys", "more.journey"));

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions { BaseUrl = Site });

		result.Failure.Should().BeNull();
		var reads = result.Checkpoints.ToDictionary(c => c.Line, c => c.Output);
		// What agent-browser 0.41.0 read from the exported batch (DECISIONS.md, "Journeys").
		reads[5].Should().Be("double clicked");
		reads[7].Should().Be("focused");
		reads[10].Should().Be("hi there");
		reads[12].Should().Be("hi there!");
		reads[15].Should().Be("area 80");
		reads[16].Should().Be("One");
		reads[17].Should().Be("Three");
		reads[18].Should().Be("Two");
		reads[19].Should().Be("x:      10\ny:      400\nwidth:  120\nheight: 30");
		reads[21].Should().Be("dropped parcel");
		reads[23].Should().Be("files note.txt");
		// agent-browser's set geo left the page without permission ("geo error 1"); this runner grants it.
		reads[29].Should().Be("at 52.37,4.89");
		reads[32].Should().Be("- link \"To about\" [url=" + Site + "about.html?from=more]");
		reads[36].Should().Be(Site + "about.html?from=more");
		reads[38].Should().Be("About");
	}

	[Test]
	public async Task GetStylesListsComputedStylesSorted()
	{
		var result = await JourneyRunner.RunAsync(Journey.Parse("open more.html\nget styles #box"), chrome.Browser, new JourneyOptions { BaseUrl = Site });

		var lines = result.Checkpoints.Single().Output.Split('\n');
		lines.Should().Contain(["color: rgb(255, 0, 0)", "width: 120px", "position: absolute"]);
		lines.Select(l => l[..l.IndexOf(':', StringComparison.Ordinal)]).Should().BeInAscendingOrder(StringComparer.Ordinal);
	}

	[Test]
	public async Task UploadsOnlyFromTheJourneysFolder()
	{
		var outside = Path.Combine(AppContext.BaseDirectory, "site", "journeys", $"outside-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(outside, "open more.html\nupload #file files/missing.txt\n");
		try
		{
			var result = await JourneyRunner.RunAsync(Journey.Load(outside), chrome.Browser, new JourneyOptions { BaseUrl = Site });
			result.Failure!.Message.Should().Be("'files/missing.txt' does not exist next to the journey.");

			var fromText = await JourneyRunner.RunAsync(Journey.Parse("open more.html\nupload #file files/note.txt"), chrome.Browser, new JourneyOptions { BaseUrl = Site });
			fromText.Failure!.Message.Should().Be("upload needs a journey loaded from a file: files are read relative to its folder.");
		}
		finally
		{
			File.Delete(outside);
		}
	}

	[Test]
	public async Task DeviceEmulationSetsViewportScaleAndUserAgent()
	{
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.SetDeviceAsync("iPhone 15");
		await page.GotoAsync(ChromeFixture.Site("more.html"));

		// screen.width: a mobile page without a viewport meta tag lays out at 980 CSS pixels whatever the device.
		(await page.EvaluateAsync("[screen.width, devicePixelRatio, navigator.userAgent.includes('iPhone'), 'ontouchstart' in window]")).GetRawText()
			.Should().Be("[393,3,true,true]");
	}

	[Test]
	public async Task AUserAgentReachesTheServerAndThePage()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);
		var journey = Journey.Parse("""
			set useragent "Mozilla/5.0 (journey) docs-test-marker"
			open /headers
			get text #ua
			get title
			""");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions { BaseUrl = new Uri(server.Url) });

		// The header the server saw, and navigator.userAgent, which the page copied into its title.
		result.Failure.Should().BeNull();
		result.Checkpoints.Select(c => c.Output).Should().Equal("Mozilla/5.0 (journey) docs-test-marker", "Mozilla/5.0 (journey) docs-test-marker");
	}

	[Test]
	public async Task HeadersAndCredentialsReachTheServer()
	{
		_ = chrome.Browser; // skips without a Chrome
		await using var server = new SlowServer(TimeSpan.Zero);
		var journey = Journey.Parse("""
			set headers '{"x-test": "from journey"}'
			open /headers
			get text #x
			set credentials admin $env:PRIVATE_PASSWORD
			open /private
			get title
			""");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions
		{
			BaseUrl = new Uri(server.Url),
			Secrets = name => name == "PRIVATE_PASSWORD" ? "hunter22" : null,
		});

		result.Failure.Should().BeNull();
		result.Checkpoints.Select(c => c.Output).Should().Equal("from journey", "Private");
	}

	[Test]
	public async Task WithoutCredentialsTheChallengeIsCancelled()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);

		var result = await JourneyRunner.RunAsync(Journey.Parse("open /private\nget title"), chrome.Browser, new JourneyOptions { BaseUrl = new Uri(server.Url) });

		result.Checkpoints.Should().NotContain(c => c.Output == "Private");
	}

	[Test]
	public async Task OfflineTakesThePageOffTheNetworkAndBackOn()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);
		var journey = Journey.Parse("open /headers\nset offline\nreload\nset offline\nreload\nget text #x");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions { BaseUrl = new Uri(server.Url) });

		// The first reload fails offline (toggled on); the toggle back lets the second through.
		result.Failure.Should().BeNull();
		result.Checkpoints.Single().Output.Should().Be("none");
	}

	[Test]
	public async Task WaitForLoadStatesAndUrls()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.FromMilliseconds(300));
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(server.Url);

		await page.WaitForLoadStateAsync("networkidle");
		await page.WaitForUrlAsync("http://localhost:*/");
		page.NavigationTimeout = TimeSpan.FromMilliseconds(300);
		await FluentActions.Awaiting(() => page.WaitForUrlAsync("**/never")).Should().ThrowAsync<TimeoutException>()
			.WithMessage("Timed out after 0s waiting for the URL to match '**/never'.");
		await FluentActions.Awaiting(() => page.WaitForLoadStateAsync("idle")).Should().ThrowAsync<ArgumentException>();
	}

	[Test]
	public async Task WaitForLoadWaitsForTheNewDocument()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.FromMilliseconds(800));
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(server.Url);

		// A navigation the page starts by itself: only the lifecycle of the new document may end the wait.
		await page.EvaluateAsync("setTimeout(() => location.href = '/next', 0)");
		await page.WaitForUrlAsync("**/next");
		await page.WaitForLoadStateAsync("load");

		(await page.EvaluateAsync("document.readyState")).GetString().Should().Be("complete");
	}

	[Test]
	[Arguments("**/about.html", "file:///x/site/about.html", true)]
	[Arguments("*/about.html", "file:///x/site/about.html", false)]
	[Arguments("http://localhost:*/", "http://localhost:5000/", true)]
	[Arguments("http://localhost:*/", "http://localhost:5000/a/", false)]
	[Arguments("**/a?c", "https://x.test/abc", true)]
	[Arguments("**/a.c", "https://x.test/abc", false)]
	public void UrlGlobs(string glob, string url, bool matches) => Page.UrlGlob(glob).IsMatch(url).Should().Be(matches);
}
