using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class FeatureBlockTests(ChromeFixture chrome)
{
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	private const string Both = "open index.html\nfeature preview\n  get title\nend\nfeature !preview\n  get url\nend\nget title\n";

	private async Task<(JourneyResult Result, List<int> Steps)> Run(string text, JourneyOptions? options = null)
	{
		var steps = new List<int>();
		var result = await JourneyRunner.RunAsync(Journey.Parse(text, "blocks.journey"), chrome.Browser,
			new JourneyOptions { BaseUrl = Site, Features = options?.Features ?? new HashSet<string>(), BuildType = options?.BuildType, Env = options?.Env, OnStep = s => steps.Add(s.Line) });
		return (result, steps);
	}

	[Test]
	public async Task ABlockRunsWhenItsFlagIsOnAndTheOtherIsSkippedOnce()
	{
		var (result, steps) = await Run(Both, new JourneyOptions { Features = new HashSet<string> { "preview" } });

		result.Failure.Should().BeNull();
		result.Checkpoints.Select(c => (c.Line, c.Command, c.Output)).Should().Equal(
			(3, "get title", "Fixture shop"), (5, "feature !preview", "skipped: !preview"), (8, "get title", "Fixture shop"));
		steps.Should().Equal(1, 3, 8).And.NotContain(6, "a skipped block's steps never start");
	}

	[Test]
	public async Task TheOtherBlockRunsWhenTheFlagIsOff()
	{
		var (result, steps) = await Run(Both);

		result.Failure.Should().BeNull();
		result.Checkpoints.Select(c => (c.Line, c.Command, c.Output)).Should().Equal(
			(2, "feature preview", "skipped: preview"), (6, "get url", Site + "index.html"), (8, "get title", "Fixture shop"));
		steps.Should().NotContain(3);
	}

	[Test]
	public async Task BuildTypeAndEnvironmentConditionsUseWhatTheRunKnows()
	{
		const string text = "open index.html\nfeature build-type:codex,assembler env:edge\nget title\nend\n";

		(await Run(text, new JourneyOptions { BuildType = "Assembler", Env = "edge" })).Result.Checkpoints.Single().Output.Should().Be("Fixture shop");
		(await Run(text, new JourneyOptions { BuildType = "assembler", Env = "prod" })).Result.Checkpoints.Single().Output.Should().Be("skipped: env:edge");
		(await Run(text)).Result.Checkpoints.Single().Output.Should().Be("skipped: build-type:codex,assembler", "a condition about something unknown does not hold");
	}

	[Test]
	public async Task AFailingStepInsideARunningBlockStopsTheRun()
	{
		var (result, _) = await Run("open index.html\nfeature preview\n  click #nothing-here\nend\nget title\n", new JourneyOptions { Features = new HashSet<string> { "preview" } });

		result.Failure!.Line.Should().Be(3);
		result.Checkpoints.Should().BeEmpty();
	}
}
