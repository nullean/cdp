using System.Text.Json;
using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Journeys;

// fixtures/journey-metadata.json is shared with js/journeys, whose parser must print the same metadata, reject the same
// text with the same errors, and judge @requires conditions the same way.
public class JourneyMetadataTests
{
	private static JsonElement Fixture(string section)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "journey-metadata.json")));
		return document.RootElement.GetProperty(section).Clone();
	}

	public static IEnumerable<Func<(string Name, string Text, string? Expected)>> Valid() =>
		Fixture("valid").EnumerateArray().Select(c => (Func<(string, string, string?)>)(() =>
			(c.GetProperty("name").GetString()!, c.GetProperty("text").GetString()!, c.GetProperty("metadata").ValueKind == JsonValueKind.Null ? null : c.GetProperty("metadata").GetRawText())));

	public static IEnumerable<Func<(string Name, string Text, string Expected)>> Invalid() =>
		Fixture("invalid").EnumerateArray().Select(c => (Func<(string, string, string)>)(() =>
			(c.GetProperty("name").GetString()!, c.GetProperty("text").GetString()!,
				string.Join('\n', c.GetProperty("errors").EnumerateArray().Select(e => $"x.journey:{e.GetProperty("line").GetInt32()}: {e.GetProperty("message").GetString()}")))));

	public static IEnumerable<Func<(string Requirement, string[] Features, string? BuildType, string? Env, bool Met)>> Requirements() =>
		Fixture("requirements").EnumerateArray().Select(c => (Func<(string, string[], string?, string?, bool)>)(() =>
			(c.GetProperty("requirement").GetString()!, [.. c.GetProperty("features").EnumerateArray().Select(f => f.GetString()!)],
				c.GetProperty("buildType").GetString(), c.GetProperty("env").GetString(), c.GetProperty("met").GetBoolean())));

	[Test]
	[MethodDataSource(nameof(Valid))]
	public void TheHeaderIsPrintedAsMetadata(string name, string text, string? expected)
	{
		_ = name;
		using var json = JsonDocument.Parse(Journey.Parse(text, "x.journey").ToJson());
		var actual = json.RootElement.TryGetProperty("metadata", out var metadata) ? metadata.GetRawText() : null;

		// The fixture is indented; compare compact, in the order ToJson writes.
		Compact(actual).Should().Be(Compact(expected));
	}

	[Test]
	[MethodDataSource(nameof(Invalid))]
	public void TheParserReportsTheFixtureErrors(string name, string text, string expected)
	{
		_ = name;
		FluentActions.Invoking(() => Journey.Parse(text, "x.journey")).Should().Throw<JourneyParseException>().Which.Message.Should().Be(expected);
	}

	[Test]
	[MethodDataSource(nameof(Requirements))]
	public void ARequirementHoldsInAContext(string requirement, string[] features, string? buildType, string? env, bool met)
	{
		var journey = Journey.Parse($"# @requires {requirement}\nopen /", "x.journey");
		var context = new JourneyContext(new HashSet<string>(features, StringComparer.Ordinal), buildType, env);

		(journey.Metadata.Unmet(context) is null).Should().Be(met);
		journey.Metadata.Requires.Single().ToString().Should().Be(requirement);
	}

	[Test]
	public void AJourneyWithoutDirectivesHasNoMetadataAndPrintsAsBefore()
	{
		var journey = Journey.Parse("# a note\nallow shop.test\nopen /\n", "x.journey");

		journey.Metadata.Should().BeSameAs(JourneyMetadata.None);
		journey.Metadata.IsEmpty.Should().BeTrue();
		journey.Metadata.Unmet(JourneyContext.Empty).Should().BeNull();
		journey.ToJson().Should().NotContain("metadata");
		journey.Steps.Should().ContainSingle();
	}

	[Test]
	public void TheMetadataIsReadable()
	{
		var metadata = Journey.Parse("# @tags a\n# @requires x !env:prod\n# @monitor schedule=5 locations=eu\n# @alert pagerduty\n# @mutates\nopen /", "x.journey").Metadata;

		metadata.Tags.Should().Equal("a");
		metadata.Requires.Select(r => r.ToString()).Should().Equal("x", "!env:prod");
		metadata.Monitor.Should().BeEquivalentTo(new JourneyMonitor(5, ["eu"]));
		metadata.Alerts.Should().Equal("pagerduty");
		metadata.Mutates.Should().BeTrue();
		metadata.Unmet(new JourneyContext(new HashSet<string> { "x" }, Env: "prod"))!.ToString().Should().Be("!env:prod");
	}

	private static string? Compact(string? json) => json is null ? null : JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
}
