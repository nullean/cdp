using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class JourneyRunnerTests(ChromeFixture chrome)
{
	// The folder, with its trailing slash: relative URLs resolve as in a browser, so ".../site" would mean its parent.
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	[Test]
	public async Task RunsEveryCommandAndReadsWhatAgentBrowserRead()
	{
		var journey = Journey.Load(Path.Combine(AppContext.BaseDirectory, "site", "journeys", "everything.journey"));

		var steps = new List<int>();
		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions { BaseUrl = Site, OnStep = s => steps.Add(s.Line) });

		result.Failure.Should().BeNull();
		steps.Should().Equal(journey.Steps.Select(s => s.Line), "OnStep sees every step, in order");
		var reads = result.Checkpoints.Where(c => !c.Command.StartsWith("snapshot", StringComparison.Ordinal)).Select(c => (c.Line, c.Output));
		// The same values agent-browser 0.41.0 printed for the exported batch (DECISIONS.md, "Journeys").
		reads.Should().Equal(
			(7, "clicked hello"), (10, "hovered"), (13, "Grace Hopper"), (17, "true"), (21, "b"), (25, "Actions"),
			(26, "Ada Lovelace"), (28, "7"), (29, "Your name"), (30, "true"), (31, "true"), (36, "About"),
			(38, Site + "actions.html"), (41, "About"));
		result.Checkpoints.Single(c => c.Line == 34).Output.Should().StartWith("- form\n  - LabelText\n  - textbox \"Your name\": Ada");
		result.Checkpoints.Should().AllSatisfy(c => c.Url.Should().StartWith(Site.AbsoluteUri));
		result.Checkpoints.Single(c => c.Line == 38).Command.Should().Be("get url");
	}

	[Test]
	public async Task AFailingStepStopsTheRunAndKeepsEarlierCheckpoints()
	{
		var journey = Journey.Parse("open actions.html\nget title\nclick #missing\nget url", "fail.journey");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser,
			new JourneyOptions { BaseUrl = Site, ActionTimeout = TimeSpan.FromMilliseconds(200) });

		result.Passed.Should().BeFalse();
		result.Checkpoints.Select(c => c.Output).Should().Equal("Actions");
		var failure = result.Failure!;
		(failure.Line, failure.Command, failure.Message).Should().Be((3, "click #missing",
			"Timed out after 0.2s waiting for css #missing to be visible and enabled: no element matches."));
		failure.Url.Should().Be(Site + "actions.html");
		failure.Describe(result.Source).Should().Be(
			"fail.journey:3: click #missing\nTimed out after 0.2s waiting for css #missing to be visible and enabled: no element matches.\n" +
			$"Page:       {Site}actions.html");
	}

	[Test]
	public async Task AFailureCarriesAScreenshotOfThePageItStoppedOn()
	{
		var journey = Journey.Parse("open actions.html\nclick #missing", "fail.journey");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser,
			new JourneyOptions { BaseUrl = Site, ActionTimeout = TimeSpan.FromMilliseconds(200) });

		result.Failure!.Screenshot.Should().NotBeNull().And.StartWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "a PNG");
		result.Failure.ScreenshotPath.Should().BeNull("there is no artifacts folder to write into");
	}

	[Test]
	public async Task AScreenshotIsWrittenToTheArtifactsFolderAndNamedInTheDescription()
	{
		var folder = Directory.CreateTempSubdirectory("cdp-failure-").FullName;
		try
		{
			var journey = Journey.Parse("open actions.html\nclick #missing", "fail.journey");

			var result = await JourneyRunner.RunAsync(journey, chrome.Browser,
				new JourneyOptions { BaseUrl = Site, ActionTimeout = TimeSpan.FromMilliseconds(200), ArtifactsDirectory = folder });

			var path = Path.Combine(folder, "failure.png");
			result.Failure!.ScreenshotPath.Should().Be(path);
			File.ReadAllBytes(path).Should().Equal(result.Failure.Screenshot);
			result.Failure.Describe(result.Source).Should().EndWith($"\nScreenshot: {path}");
		}
		finally
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	[Test]
	public async Task AScreenshotCanBeTurnedOff()
	{
		var journey = Journey.Parse("open actions.html\nclick #missing", "fail.journey");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser,
			new JourneyOptions { BaseUrl = Site, ActionTimeout = TimeSpan.FromMilliseconds(200), ScreenshotOnFailure = false });

		result.Failure!.Screenshot.Should().BeNull();
		result.Failure.Url.Should().Be(Site + "actions.html", "the page's address is still recorded");
	}

	[Test]
	public async Task ARelativeOpenWithoutABaseUrlFailsAtItsStep()
	{
		var result = await JourneyRunner.RunAsync(Journey.Parse("open /x", "rel.journey"), chrome.Browser);

		var failure = result.Failure!;
		(failure.Line, failure.Command, failure.Message).Should().Be((1, "open /x", "rel.journey:1: '/x' is relative: give a base URL"));
	}

	[Test]
	public async Task ANavigationErrorIsAStepFailure()
	{
		var result = await JourneyRunner.RunAsync(Journey.Parse("open missing.html"), chrome.Browser, new JourneyOptions { BaseUrl = Site });

		result.Failure!.Message.Should().EndWith("failed: net::ERR_FILE_NOT_FOUND");
	}

	[Test]
	public async Task CallerCancellationIsThrownNotReported()
	{
		var browser = chrome.Browser; // skips without a Chrome, outside the assertion below
		using var cancel = new CancellationTokenSource();
		await cancel.CancelAsync();

		var act = () => JourneyRunner.RunAsync(Journey.Parse("open actions.html"), browser, new JourneyOptions { BaseUrl = Site }, cancel.Token);
		await act.Should().ThrowAsync<OperationCanceledException>();
	}
}
