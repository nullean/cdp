using System.Text.Json;
using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Journeys;

// fixtures/journey-blocks.json is shared with js/journeys, whose parser must produce the same blocks and reject the same
// text with the same errors.
public class JourneyBlockTests
{
	private static JsonElement Fixture(string section)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "journey-blocks.json")));
		return document.RootElement.GetProperty(section).Clone();
	}

	public static IEnumerable<Func<(string Name, string Text, int Version, string? Blocks, int?[] StepBlocks)>> Valid() =>
		Fixture("valid")
			.EnumerateArray()
			.Select(
				c =>
					(Func<(string, string, int, string?, int?[])>)(() =>
						(c.GetProperty("name").GetString()!, c.GetProperty("text").GetString()!, c.GetProperty("version").GetInt32(), c.GetProperty(
							"blocks"
						).ValueKind == JsonValueKind.Null ? null : c.GetProperty("blocks").GetRawText(), [
							.. c.GetProperty("stepBlocks").EnumerateArray().Select(b => b.ValueKind == JsonValueKind.Null ? (int?)null : b.GetInt32())
						]))
			);

	public static IEnumerable<Func<(string Name, string Text, string Expected)>> Invalid() =>
		Fixture("invalid")
			.EnumerateArray()
			.Select(
				c =>
					(Func<(string, string, string)>)(() =>
						(c.GetProperty("name").GetString()!, c.GetProperty("text").GetString()!, string.Join(
							'\n',
							c
								.GetProperty("errors")
								.EnumerateArray()
								.Select(e => $"x.journey:{e.GetProperty("line").GetInt32()}: {e.GetProperty("message").GetString()}")
						)))
			);

	[Test]
	[MethodDataSource(nameof(Valid))]
	public void TheBlocksArePrintedAsJson(string name, string text, int version, string? blocks, int?[] stepBlocks)
	{
		_ = name;
		var journey = Journey.Parse(text, "x.journey");
		using var json = JsonDocument.Parse(journey.ToJson());

		json.RootElement.GetProperty("version").GetInt32().Should().Be(version);
		(json.RootElement.TryGetProperty("blocks", out var actual) ? Compact(actual.GetRawText()) : null).Should().Be(Compact(blocks));
		journey.Steps.Select(s => s.Block?.Line).Should().Equal(stepBlocks);
		json
			.RootElement
			.GetProperty("steps")
			.EnumerateArray()
			.Select(s => s.TryGetProperty("block", out var b) ? b.GetInt32() : (int?)null)
			.Should()
			.Equal(stepBlocks);
	}

	[Test]
	[MethodDataSource(nameof(Invalid))]
	public void TheParserReportsTheFixtureErrors(string name, string text, string expected)
	{
		_ = name;
		FluentActions.Invoking(() => Journey.Parse(text, "x.journey")).Should().Throw<JourneyParseException>().Which.Message.Should().Be(expected);
	}

	[Test]
	public void AJourneyWithoutBlocksPrintsExactlyAsBefore()
	{
		var json = Journey.Parse("allow shop.test\nopen /\n", "x.journey").ToJson();

		json.Should().Contain("\"version\": 1").And.NotContain("block");
	}

	[Test]
	public void ABlockKnowsWhyItDoesNotApply()
	{
		var block = Journey.Parse("feature a !env:prod\nclick #a\nend\n", "x.journey").Blocks.Single();

		block.Text.Should().Be("feature a !env:prod");
		block.Unmet(new JourneyContext(new HashSet<string> { "a" }, Env: "prod"))!.ToString().Should().Be("!env:prod");
		block.Unmet(new JourneyContext(new HashSet<string> { "a" }, Env: "edge")).Should().BeNull();
		block.Unmet(JourneyContext.Empty)!.ToString().Should().Be("a");
	}

	[Test]
	public void AgentBrowserExportRefusesBlocks()
	{
		FluentActions
			.Invoking(() => Journey.Parse("open /\nfeature a\nclick #a\nend\n", "x.journey").ToAgentBrowserBatch(new Uri("http://localhost/")))
			.Should()
			.Throw<JourneyParseException>()
			.Which
			.Message
			.Should()
			.Be("x.journey:2: feature blocks are not part of agent-browser, which runs every line; use one journey file per flag state to export");
	}

	private static string? Compact(string? json) => json is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
}
