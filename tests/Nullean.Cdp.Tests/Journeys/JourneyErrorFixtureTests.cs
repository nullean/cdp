using System.Text.Json;
using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Journeys;

// fixtures/journey-errors.json is shared with js/journeys, whose parser must reject the same text with the same errors.
public class JourneyErrorFixtureTests
{
	public static IEnumerable<Func<(string Name, string Text, string Expected)>> Cases()
	{
		using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "journey-errors.json")));
		foreach (var c in document.RootElement.GetProperty("cases").EnumerateArray().ToArray())
		{
			var name = c.GetProperty("name").GetString()!;
			var text = c.GetProperty("text").GetString()!;
			var expected = string.Join('\n', c.GetProperty("errors").EnumerateArray().Select(e => $"x.journey:{e.GetProperty("line").GetInt32()}: {e.GetProperty("message").GetString()}"));
			yield return () => (name, text, expected);
		}
	}

	[Test]
	[MethodDataSource(nameof(Cases))]
	public void TheParserReportsTheFixtureErrors(string name, string text, string expected)
	{
		_ = name;
		FluentActions.Invoking(() => Journey.Parse(text, "x.journey")).Should().Throw<JourneyParseException>().Which.Message.Should().Be(expected);
	}
}
