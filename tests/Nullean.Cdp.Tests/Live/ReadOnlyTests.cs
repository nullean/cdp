using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class ReadOnlyTests(ChromeFixture chrome)
{
	private async Task<(JourneyResult Result, int Writes)> Run(string journey, bool readOnly = true, params string[] writable)
	{
		await using var server = new WriteServer();
		var result = await JourneyRunner.RunAsync(Journey.Parse(journey, "read-only.journey"), chrome.Browser, new JourneyOptions
		{
			BaseUrl = new Uri(server.Url),
			ReadOnly = readOnly,
			WritableUrls = writable.Select(w => server.Url + w).ToArray(),
			ActionTimeout = TimeSpan.FromSeconds(3),
		});
		await Task.Delay(100);
		return (result, server.Writes);
	}

	[Test]
	public async Task AFetchPostFailsTheStepAndNeverReachesTheServer()
	{
		var (result, writes) = await Run("open /\nfind role button click --name \"Save with fetch\"\nwait 300\nget title");

		result.Failure.Should().NotBeNull();
		result.Failure!.Line.Should().BeOneOf(2, 3);
		result.Failure.Message.Should().StartWith("Blocked POST http://localhost:").And.EndWith("/write: this run is read-only, so only GET, HEAD and OPTIONS requests are sent (JourneyOptions.WritableUrls, or --writable on the CLI, allows a URL).");
		writes.Should().Be(0);
	}

	[Test]
	public async Task AFormPostFailsTheStepAndNeverReachesTheServer()
	{
		var (result, writes) = await Run("open /\nfind role button click --name \"Save with form\"\nget title");

		result.Failure.Should().NotBeNull();
		result.Failure!.Message.Should().StartWith("Blocked POST http://localhost:").And.Contain("/form: this run is read-only");
		writes.Should().Be(0);
	}

	[Test]
	public async Task ReadsStillWork()
	{
		var (result, writes) = await Run("open /\nfind role link click --name \"Next page\"\nwait --url **/next\nget title");

		result.Failure.Should().BeNull();
		result.Checkpoints.Single().Output.Should().Be("Next");
		writes.Should().Be(0);
	}

	[Test]
	public async Task AWritableUrlLetsItThrough()
	{
		var (result, writes) = await Run("open /\nfind role button click --name \"Send telemetry\"\nwait 300\nget title", true, "telemetry");

		result.Failure.Should().BeNull();
		writes.Should().Be(1);
	}

	[Test]
	public async Task WritableUrlsDoNotLetOtherWritesThrough()
	{
		var (result, writes) = await Run("open /\nfind role button click --name \"Save with fetch\"\nwait 300\nget title", true, "telemetry");

		result.Failure.Should().NotBeNull();
		writes.Should().Be(0);
	}

	[Test]
	public async Task WithoutReadOnlyTheWriteGoesThrough()
	{
		var (result, writes) = await Run("open /\nfind role button click --name \"Save with fetch\"\nwait 300\nget title", readOnly: false);

		result.Failure.Should().BeNull();
		writes.Should().Be(1);
	}
}
