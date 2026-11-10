using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class RunDefaultsTests(ChromeFixture chrome)
{
	private const string Reads = "open /echo\nget text #ua\nget text #width\nget text #header\nget text #journey\nget text #request-ua\n";

	private static readonly JourneyOptions Monitor = new()
	{
		Headers = new Dictionary<string, string> { ["X-Docs-Synthetic-Monitor"] = "true" },
		UserAgent = "Elastic/Synthetics",
		Viewport = new JourneyViewport(1111, 700),
	};

	// What the page saw (user agent, width) and what the server saw (header, journey header, user agent).
	private async Task<string[]> Run(string text, JourneyOptions defaults)
	{
		await using var server = new EchoServer();
		var result = await JourneyRunner.RunAsync(Journey.Parse(text, "defaults.journey"), chrome.Browser, new JourneyOptions
		{
			BaseUrl = new Uri(server.Url),
			Headers = defaults.Headers,
			UserAgent = defaults.UserAgent,
			Viewport = defaults.Viewport,
			ActionTimeout = TimeSpan.FromSeconds(3),
		});
		result.Failure.Should().BeNull();
		return [.. result.Checkpoints.Where(c => c.Command.StartsWith("get text", StringComparison.Ordinal)).Select(c => c.Output)];
	}

	[Test]
	public async Task TheRunStartsWithItsViewportUserAgentAndHeaders() =>
		(await Run(Reads, Monitor)).Should().Equal("Elastic/Synthetics", "1111", "true", "none", "Elastic/Synthetics");

	[Test]
	public async Task WithoutDefaultsNothingChanges()
	{
		var seen = await Run(Reads, new JourneyOptions());

		seen[0].Should().Contain("Chrome").And.NotContain("Elastic/Synthetics");
		seen[2].Should().Be("none");
		int.Parse(seen[1], System.Globalization.CultureInfo.InvariantCulture).Should().NotBe(1111);
	}

	[Test]
	public async Task AJourneysHeadersAddToTheRunsAndWinWhenTheyNameTheSame() =>
		(await Run("set headers '{\"x-journey\": \"1\", \"X-Docs-Synthetic-Monitor\": \"override\"}'\n" + Reads, Monitor))
			.Should().Equal("Elastic/Synthetics", "1111", "override", "1", "Elastic/Synthetics");

	[Test]
	public async Task AJourneysHeadersKeepTheRunsOthers() =>
		(await Run("set headers '{\"x-journey\": \"1\"}'\n" + Reads, Monitor)).Skip(2).Should().Equal("true", "1", "Elastic/Synthetics");

	[Test]
	public async Task AJourneysUserAgentReplacesTheRunsAndTheHeaderStays() =>
		(await Run("set useragent Other/1.0\n" + Reads, Monitor)).Should().Equal("Other/1.0", "1111", "true", "none", "Other/1.0");

	[Test]
	public async Task AJourneysViewportReplacesTheRuns() =>
		(await Run("set viewport 800 600\n" + Reads, Monitor))[1].Should().Be("800");

	[Test]
	public async Task ADeviceKeepsTheRunsUserAgentButWithoutOneBringsItsOwn()
	{
		(await Run("set device 'iPhone 14'\n" + Reads, Monitor))[0].Should().Be("Elastic/Synthetics");
		(await Run("set device 'iPhone 14'\n" + Reads, new JourneyOptions()))[0].Should().Contain("iPhone");
	}
}
