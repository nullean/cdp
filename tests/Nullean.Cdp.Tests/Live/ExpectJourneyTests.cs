using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

// `expect` lines in journeys: the same engine as Locator.CheckAsync, reported as a step failure, and never a checkpoint.
[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class ExpectJourneyTests(ChromeFixture chrome)
{
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	private Task<JourneyResult> RunAsync(string journey, Func<string, string?>? secrets = null) =>
		JourneyRunner.RunAsync(Journey.Parse(journey, "x.journey"), chrome.Browser, new JourneyOptions { BaseUrl = Site, Secrets = secrets ?? (_ => null) });

	[Test]
	public async Task PassingAssertionsRecordNothing()
	{
		var result = await RunAsync(
			"""
			open assertions.html
			expect visible #go
			expect text #count "2 results"
			expect visible #late
			expect title Loaded
			expect --not visible #gone
			expect count li 3
			expect visible find role button --name Go --exact
			get text #count
			"""
		);

		result.Failure.Should().BeNull();
		// Only the read is a checkpoint: a passing assertion changes no verified transcript.
		result.Checkpoints.Select(c => c.Command).Should().Equal("get text #count");
	}

	[Test]
	public async Task AFailingAssertionStopsTheJourneyAtItsLine()
	{
		var result = await RunAsync(
			"""
			open assertions.html
			get text #count
			expect --timeout 200 text #count 3
			get title
			"""
		);

		result.Checkpoints.Select(c => c.Command).Should().Equal("get text #count");
		result.Failure.Should().NotBeNull();
		result.Failure!.Line.Should().Be(3);
		result.Failure.Command.Should().Be("expect --timeout 200 text #count 3");
		result.Failure.Message.Should().Be("Timed out after 0.2s waiting for css #count to have text \"3\": received \"2 results\".");
	}

	[Test]
	public async Task TheJourneyTimeoutIsThePagesActionTimeoutUnlessGiven()
	{
		var result = await JourneyRunner.RunAsync(
			Journey.Parse("open assertions.html\nexpect visible #nothing"),
			chrome.Browser,
			new JourneyOptions { BaseUrl = Site, ActionTimeout = TimeSpan.FromMilliseconds(300) }
		);

		result.Failure!.Message.Should().Be("Timed out after 0.3s waiting for css #nothing to be visible: received no element matches.");
	}

	[Test]
	public async Task SecretsInExpectedValuesAreResolvedAndRedacted()
	{
		var result = await RunAsync(
			"""
			open assertions.html
			fill #name $env:NJ_SECRET
			expect value #name $env:NJ_SECRET
			expect --timeout 200 value #name nope
			""",
			name => name == "NJ_SECRET" ? "Ada-secret-value" : null
		);

		result.Failure!.Line.Should().Be(4);
		result.Failure.Message.Should().Be("Timed out after 0.2s waiting for css #name to have value \"nope\": received \"$env:NJ_SECRET\".");
	}
}
