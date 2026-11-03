using AwesomeAssertions;
using CsCheck;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Journeys;

public class CommandLineTests
{
	private static IReadOnlyList<string>? Split(string line) => CommandLine.Split(line, out _);

	[Test]
	public void JoinThenSplitGivesTheArgumentsBack() =>
		Gen.String[Gen.Char[" \t\"'\\#ab@é-"], 0, 6].Array[1, 6]
			.Sample(args => Split(CommandLine.Join(args)).Should().Equal(args));

	[Test]
	[Arguments("open /", new[] { "open", "/" })]
	[Arguments("  fill   #q   \"two words\"  ", new[] { "fill", "#q", "two words" })]
	[Arguments("find text 'it''s' click", new[] { "find", "text", "its", "click" })]
	[Arguments("type #q \"say \\\"hi\\\" \\\\ \\n\"", new[] { "type", "#q", "say \"hi\" \\ \\n" })]
	[Arguments("click a\\ b", new[] { "click", "a b" })]
	[Arguments("get url # trailing comment", new[] { "get", "url" })]
	[Arguments("click a#b", new[] { "click", "a#b" })]
	[Arguments("fill #q \"\"", new[] { "fill", "#q", "" })]
	[Arguments("fill #q pre\"fix \"'and' x", new[] { "fill", "#q", "prefix and", "x" })]
	[Arguments("   # only a comment", new string[0])]
	[Arguments("click #go # the id selector stays", new[] { "click", "#go" })]
	[Arguments("click #", new[] { "click" })]
	[Arguments("fill #q \"#\"", new[] { "fill", "#q", "#" })]
	public void Splits(string line, string[] expected) => Split(line).Should().Equal(expected);

	[Test]
	public void UnclosedQuotesAreErrors()
	{
		CommandLine.Split("fill #q \"open", out var error).Should().BeNull();
		error.Should().Be("the double quote is not closed");
		CommandLine.Split("fill #q 'open", out error).Should().BeNull();
		error.Should().Be("the single quote is not closed");
	}

	[Test]
	public void JoinQuotesOnlyWhatNeedsIt() =>
		CommandLine.Join(["find", "role", "button", "--name", "Say \"hi\"", "", "#x", "#"])
			.Should().Be("find role button --name \"Say \\\"hi\\\"\" \"\" #x \"#\"");

	[Test]
	public void LineBreaksCannotBeJoined()
	{
		var act = () => CommandLine.Join(["fill", "#q", "two\nlines"]);
		act.Should().Throw<ArgumentException>();
	}
}
