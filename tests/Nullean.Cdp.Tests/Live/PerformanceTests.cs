using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class PerformanceTests(ChromeFixture chrome)
{
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	private async Task<(BrowserContext, Page)> Open(string url)
	{
		var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(url);
		return (context, page);
	}

	[Test]
	public async Task TimingsFollowTheLoad()
	{
		var skipWithoutChrome = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.FromMilliseconds(300));
		var (context, page) = await Open(server.Url);
		await using var _ = context;

		var t = await page.TimingsAsync();

		t.Named.Should().AllSatisfy(r => r.Value.Should().NotBeNull($"{r.Name} is reached once the page has loaded"));
		t.TimeToFirstByte.Should().BeLessThanOrEqualTo(t.FirstContentfulPaint!.Value);
		t.DomInteractive.Should().BeLessThanOrEqualTo(t.DomContentLoaded!.Value);
		t.Load.Should().BeGreaterThanOrEqualTo(300, "the image answers 300 ms late and load waits for it");
	}

	[Test]
	public async Task VitalsMeasureLcpShiftsAndInteractions()
	{
		var (context, page) = await Open(ChromeFixture.Site("perf.html"));
		await using var _ = context;
		await page.Text("Late banner").WaitAsync();
		await Task.Delay(300); // the shift is reported once its frame is rendered
		await page.Role("button", "Slow button").ClickAsync();
		await page.Text("Clicked").WaitAsync();

		var v = await page.VitalsAsync();

		v.FirstContentfulPaint.Should().NotBeNull();
		v.LargestContentfulPaint.Should().NotBeNull();
		v.LargestContentfulPaintElement.Should().NotBeNullOrEmpty();
		v.CumulativeLayoutShift.Should().BeGreaterThan(0, "the late banner shifts the page");
		v.InteractionToNextPaint.Should().BeGreaterThanOrEqualTo(80, "the click handler blocks for 80 ms");
	}

	[Test]
	public async Task AStillPageHasNoShiftsAndNoInteraction()
	{
		var (context, page) = await Open(ChromeFixture.Site("about.html"));
		await using var _ = context;

		var v = await page.VitalsAsync();

		v.CumulativeLayoutShift.Should().Be(0);
		v.InteractionToNextPaint.Should().BeNull();
	}

	[Test]
	public async Task MetricsAreChromesCounters()
	{
		var (context, page) = await Open(ChromeFixture.Site("index.html"));
		await using var _ = context;

		var m = await page.MetricsAsync();

		m.Should().ContainKeys("Nodes", "JSEventListeners", "LayoutCount", "ScriptDuration", "JSHeapUsedSize");
		m["Nodes"].Should().BeGreaterThan(10);
	}

	[Test]
	public async Task JourneysPrintRawReadingsOrBudgets()
	{
		var journey = Journey.Parse("""
			open perf.html
			wait --text "Late banner"
			wait 300
			timings
			vitals --budget lcp=60000 cls=1
			metrics --budget Nodes=100000
			""");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions { BaseUrl = Site });

		result.Failure.Should().BeNull();
		result.Checkpoints[0].Output.Split('\n').Select(l => l.Split(':')[0]).Should().Equal("ttfb", "first-paint", "fcp", "dom-interactive", "dom-content-loaded", "load");
		result.Checkpoints[0].Output.Should().MatchRegex(@"^ttfb: [\d.]+ms\n");
		result.Checkpoints[1].Output.Should().Be("lcp ok (<= 60000ms)\ncls ok (<= 1)");
		result.Checkpoints[2].Output.Should().Be("Nodes ok (<= 100000)");
	}

	[Test]
	public async Task AReadingOverItsBudgetFailsTheStep()
	{
		// Chrome reports a layout shift once the frame is rendered, which can be a moment after the banner is laid out.
		var journey = Journey.Parse("open perf.html\nwait --text \"Late banner\"\nwait 300\nvitals --budget cls=0 inp=200", "perf.journey");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions { BaseUrl = Site });

		result.Failure!.Line.Should().Be(4);
		result.Failure.Message.Should().MatchRegex(@"^Over budget: cls 0\.\d+ > 0; inp has no value yet \(budget 200ms\)\.$");
	}
}
