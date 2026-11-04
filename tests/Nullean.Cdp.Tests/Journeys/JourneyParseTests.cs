using AwesomeAssertions;
using Nullean.Cdp.Accessibility;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots;

namespace Nullean.Cdp.Tests.Journeys;

public class JourneyParseTests
{
	private static JourneyCommand One(string line) => Journey.Parse(line).Steps.Single().Command;

	private static string Error(string text) =>
		FluentActions.Invoking(() => Journey.Parse(text, "x.journey")).Should().Throw<JourneyParseException>().Which.Message;

	[Test]
	public void ParsesEveryCommand()
	{
		One("open /docs").Should().Be(new OpenCommand("/docs"));
		One("goto https://x.test/").Should().Be(new OpenCommand("https://x.test/"));
		One("back").Should().Be(new HistoryCommand("back"));
		One("reload").Should().Be(new HistoryCommand("reload"));
		One("click #go").Should().BeEquivalentTo(new ActCommand("click", new JourneyTarget(LocatorKind.Css, "#go"), []));
		One("fill #q \"red lamp\"").Should().BeEquivalentTo(new ActCommand("fill", new JourneyTarget(LocatorKind.Css, "#q"), ["red lamp"]));
		One("select #colour g b").Should().BeEquivalentTo(new ActCommand("select", new JourneyTarget(LocatorKind.Css, "#colour"), ["g", "b"]));
		One("press Control+a").Should().Be(new PressCommand("Control+a"));
		One("key Enter").Should().Be(new PressCommand("Enter"));
		One("wait 250").Should().Be(new WaitCommand(null, 250));
		One("wait main").Should().Be(new WaitCommand(new JourneyTarget(LocatorKind.Css, "main"), null));
		One("wait --text \"3 results\"").Should().Be(new WaitCommand(new JourneyTarget(LocatorKind.Text, "3 results"), null));
		One("set viewport 800 600").Should().Be(new SetCommand("viewport", 800, 600));
		One("set media dark").Should().Be(new SetCommand("media", Scheme: "dark"));
		One("snapshot -c -i -d 2 -s main").Should().Be(new SnapshotCommand(new AxSnapshotOptions { Compact = true, Interactive = true, Depth = 2, Selector = "main" }));
		One("get title").Should().Be(new ReadCommand("title", null));
		One("get attr #q placeholder").Should().Be(new ReadCommand("attr", new JourneyTarget(LocatorKind.Css, "#q"), "placeholder"));
		One("get count li").Should().Be(new ReadCommand("count", new JourneyTarget(LocatorKind.Css, "li")));
		One("is checked #news").Should().Be(new ReadCommand("is checked", new JourneyTarget(LocatorKind.Css, "#news")));
	}

	[Test]
	public void ParsesFind()
	{
		One("find role button --name Go").Should().BeEquivalentTo(new ActCommand("click", new JourneyTarget(LocatorKind.Role, "button", "Go"), []));
		One("find text \"Sign in\" click --exact").Should().BeEquivalentTo(new ActCommand("click", new JourneyTarget(LocatorKind.Text, "Sign in", null, true), []));
		One("find label Email fill a@b.test").Should().BeEquivalentTo(new ActCommand("fill", new JourneyTarget(LocatorKind.Label, "Email"), ["a@b.test"]));
		One("find testid save hover").Should().BeEquivalentTo(new ActCommand("hover", new JourneyTarget(LocatorKind.TestId, "save"), []));
		One("find role heading text --name Welcome").Should().Be(new ReadCommand("text", new JourneyTarget(LocatorKind.Role, "heading", "Welcome")));
	}

	[Test]
	public void CheckpointsAreTheReads()
	{
		var journey = Journey.Parse("open /\nclick #a\nsnapshot\nget url\nis visible #a\nfind role link text\nwait 1");
		journey.Steps.Select(s => s.Command.IsCheckpoint).Should().Equal(false, false, true, true, true, true, false);
	}

	[Test]
	public void StepsKeepTheirLineAndANormalisedText()
	{
		var journey = Journey.Parse("# a journey\n\nopen  /   # home\n  find role button   --name  'Say hello'\r\n");
		journey.Steps.Select(s => (s.Line, s.Text)).Should().Equal((3, "open /"), (4, "find role button --name \"Say hello\""));
	}

	[Test]
	public void RefsAreRejected() =>
		Error("click @e12").Should().Be(
			"x.journey:1: '@e12' is an agent-browser ref, which changes from run to run; use find role <role> --name <name>, find text, or a CSS selector.");

	[Test]
	public void EveryErrorIsReportedWithItsLine() =>
		Error("open /\nscreenshot a.png\nfill #q\nfind nth 2 li click\nclick \"open").Should().Be(
			"x.journey:2: 'screenshot' is not supported in journeys. Supported: allow, open, back, forward, reload, click, hover, fill, type, check, uncheck, select, press, find, wait, set, snapshot, get, is.\n" +
			"x.journey:3: usage: fill <selector> <text>\n" +
			"x.journey:4: find nth is not supported in journeys; use a CSS selector that matches one element.\n" +
			"x.journey:5: the double quote is not closed");

	[Test]
	[Arguments("find role button press", "find does not support 'press': use click, fill, check, hover or text")]
	[Arguments("find text Go --name x", "--name only applies to find role")]
	[Arguments("find label Email fill", "find … fill needs the text to fill")]
	[Arguments("find role", "usage: find role|text|label|placeholder|alt|title|testid <value> [click|fill|check|hover|text] [text] [--name <name>] [--exact]")]
	[Arguments("find role button --name", "--name needs a value")]
	[Arguments("wait --load networkidle", "usage: wait <selector>|<ms>|--text <text>")]
	[Arguments("wait --url", "wait --url is not supported in journeys; use wait <selector>, wait <ms> or wait --text <text>.")]
	[Arguments("set device iPhone", "set device is not supported in journeys; use set viewport or set media.")]
	[Arguments("set viewport 0 600", "usage: set viewport <width> <height>")]
	[Arguments("set media sepia", "usage: set media dark|light")]
	[Arguments("snapshot -u", "snapshot -u is not supported in journeys; use -i, -c, -d <n> or -s <selector>.")]
	[Arguments("snapshot -d", "-d needs a number")]
	[Arguments("snapshot -s @e3", "'@e3' is an agent-browser ref, which changes from run to run; use find role <role> --name <name>, find text, or a CSS selector.")]
	[Arguments("get box #a", "get box is not supported in journeys: get text|html|value|count <selector> | get attr <selector> <name> | get title | get url")]
	[Arguments("get text", "usage: get text|html|value|count <selector> | get attr <selector> <name> | get title | get url")]
	[Arguments("is hidden #a", "usage: is visible|enabled|checked <selector>")]
	[Arguments("back now", "usage: back")]
	public void RejectsWithAReason(string line, string message) => Error(line).Should().Be("x.journey:1: " + message);

	[Test]
	public void ExportsAgentBrowserBatchJson()
	{
		var journey = Journey.Parse("""
			open /shop
			open docs?page=2
			open https://elsewhere.test/a
			find role button --name "Say hello"
			fill #q "red \"lamp\""
			snapshot -c
			""");

		journey.ToAgentBrowserBatch(new Uri("http://localhost:5000/app/")).Should().MatchSnapshot();
	}

	[Test]
	public void RelativeUrlsNeedABaseUrl()
	{
		var journey = Journey.Parse("click #a\nopen docs", "x.journey");
		var act = () => journey.ToAgentBrowserBatch();
		act.Should().Throw<JourneyParseException>().WithMessage("x.journey:2: 'docs' is relative: give a base URL");
	}
}
