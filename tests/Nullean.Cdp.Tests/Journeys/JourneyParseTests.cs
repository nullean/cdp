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
	public void ParsesTheRestOfAgentBrowsersCommands()
	{
		var css = (string s) => new JourneyTarget(LocatorKind.Css, s);
		One("dblclick #a").Should().BeEquivalentTo(new ActCommand("dblclick", css("#a"), []));
		One("focus #a").Should().BeEquivalentTo(new ActCommand("focus", css("#a"), []));
		One("scrollintoview #a").Should().BeEquivalentTo(new ActCommand("scrollintoview", css("#a"), []));
		One("scrollinto #a").Should().BeEquivalentTo(new ActCommand("scrollintoview", css("#a"), []));
		One("upload #file files/a.txt b.txt").Should().BeEquivalentTo(new ActCommand("upload", css("#file"), ["files/a.txt", "b.txt"]));
		One("drag #from #to").Should().Be(new DragCommand(css("#from"), css("#to")));
		One("scroll").Should().Be(new ScrollCommand("down", 300, null));
		One("scroll up 120 -s .list").Should().Be(new ScrollCommand("up", 120, css(".list")));
		One("scroll 50").Should().Be(new ScrollCommand("down", 50, null));
		One("keyboard type hello").Should().Be(new KeyboardCommand("type", "hello"));
		One("keyboard inserttext $env:TOKEN").Should().Be(new KeyboardCommand("inserttext", "$env:TOKEN"));
		One("wait --url \"**/done\"").Should().Be(new WaitCommand(null, null, Url: "**/done"));
		One("wait --load networkidle").Should().Be(new WaitCommand(null, null, LoadState: "networkidle"));
		One("set viewport 800 600 2").Should().Be(new SetCommand("viewport", 800, 600) { Scale = 2 });
		One("set device \"iPhone 15\"").Should().Be(new SetCommand("device") { Device = "iPhone 15" });
		One("set geo 52.37 4.89").Should().Be(new SetCommand("geo") { Position = (52.37, 4.89) });
		One("set offline").Should().Be(new SetCommand("offline"));
		One("set offline off").Should().Be(new SetCommand("offline") { Offline = false });
		One("set headers '{\"x-test\": \"1\"}'").Should().Be(new SetCommand("headers") { Headers = "{\"x-test\": \"1\"}" });
		One("set credentials admin $env:PW").Should().Be(new SetCommand("credentials") { User = "admin", Password = "$env:PW" });
		One("snapshot -u -c").Should().Be(new SnapshotCommand(new AxSnapshotOptions { Urls = true, Compact = true }));
		One("get box #a").Should().Be(new ReadCommand("box", css("#a")));
		One("get styles #a").Should().Be(new ReadCommand("styles", css("#a")));
		One("timings").Should().Be(new PerformanceCommand("timings", []));
		One("vitals --budget lcp=2500 cls=0.1").Should().Be(new PerformanceCommand("vitals", [new PerformanceBudget("lcp", 2500), new PerformanceBudget("cls", 0.1)]));
		One("metrics --budget Nodes=1500").Should().Be(new PerformanceCommand("metrics", [new PerformanceBudget("Nodes", 1500)]));
		One("network har start").Should().Be(new HarCommand("start"));
		One("network har stop out/run.har").Should().Be(new HarCommand("stop", "out/run.har"));
		One("network requests").Should().Be(new RequestsCommand(false, null, [], null, null));
		One("network requests --clear").Should().Be(new RequestsCommand(true, null, [], null, null));
		One("network requests --filter api --type xhr,fetch --method post --status 4xx")
			.Should().Be(new RequestsCommand(false, "api", ["xhr", "fetch"], "POST", "4xx"));
		One("console").Should().Be(new ReadCommand("console", null));
		One("errors").Should().Be(new ReadCommand("errors", null));
		One("console --clear").Should().Be(new ClearLogCommand("console"));
		One("errors --clear").Should().Be(new ClearLogCommand("errors"));
	}

	[Test]
	public void ParsesFindFirstLastAndNth()
	{
		One("find first \"nav a\" text").Should().Be(new ReadCommand("text", new JourneyTarget(LocatorKind.Css, "nav a", Index: 0)));
		One("find last li").Should().BeEquivalentTo(new ActCommand("click", new JourneyTarget(LocatorKind.Css, "li", Index: -1), []));
		One("find nth 2 .card hover").Should().BeEquivalentTo(new ActCommand("hover", new JourneyTarget(LocatorKind.Css, ".card", Index: 2), []));
		One("find nth 1 input fill hi").Should().BeEquivalentTo(new ActCommand("fill", new JourneyTarget(LocatorKind.Css, "input", Index: 1), ["hi"]));
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
		Error("open /\nscreenshot a.png\nfill #q\nfind nth li\nclick \"open").Should().Be(
			"x.journey:2: 'screenshot' is not supported in journeys. Supported: allow, open, back, forward, reload, click, dblclick, hover, focus, scrollintoview, fill, type, check, uncheck, select, upload, drag, scroll, press, keyboard, find, wait, set, snapshot, get, console, errors, network, timings, vitals, metrics, is.\n" +
			"x.journey:3: usage: fill <selector> <text>\n" +
			"x.journey:4: usage: find nth <n> <selector> [action] [text]\n" +
			"x.journey:5: the double quote is not closed");

	[Test]
	[Arguments("find role button press", "find does not support 'press': use click, fill, check, hover or text")]
	[Arguments("find text Go --name x", "--name only applies to find role")]
	[Arguments("find label Email fill", "find … fill needs the text to fill")]
	[Arguments("find role", "usage: find role|text|label|placeholder|alt|title|testid <value> | first|last <selector> | nth <n> <selector> [click|fill|check|hover|text] [text] [--name <name>] [--exact]")]
	[Arguments("find nth x li", "usage: find nth <n> <selector> [action] [text]")]
	[Arguments("find first @e3 click", "'@e3' is an agent-browser ref, which changes from run to run; use find role <role> --name <name>, find text, or a CSS selector.")]
	[Arguments("find role button --name", "--name needs a value")]
	[Arguments("wait --load idle", "usage: wait <selector>|<ms>|--text <text>|--url <glob>|--load load|domcontentloaded|networkidle")]
	[Arguments("wait --url", "usage: wait <selector>|<ms>|--text <text>|--url <glob>|--load load|domcontentloaded|networkidle")]
	[Arguments("wait --fn x", "wait --fn is not supported in journeys: it runs page script or saves files.")]
	[Arguments("set device Nokia", "unknown device 'Nokia'; known: Desktop Chrome, Galaxy S9+, Pixel 5, Pixel 7, iPad Mini, iPad Pro 11, iPhone 12, iPhone 13, iPhone 14, iPhone 15, iPhone 15 Pro Max, iPhone SE")]
	[Arguments("set viewport 0 600", "usage: set viewport <width> <height> [scale]")]
	[Arguments("set geo 91 0", "usage: set geo <latitude> <longitude>")]
	[Arguments("set headers [1]", "set headers needs a JSON object of string values, e.g. '{\"x-test\": \"1\"}'")]
	[Arguments("set credentials admin hunter22", "set credentials needs a secret reference for the password ($env:NAME), not a literal")]
	[Arguments("set cookie x", "set cookie is not supported in journeys: set viewport <w> <h> [scale] | media dark|light | device <name> | geo <lat> <lng> | offline [on|off] | headers <json> | credentials <user> <password>")]
	[Arguments("upload #f ../secret.txt", "'../secret.txt' must be a path inside the journey's folder, relative to it.")]
	[Arguments("upload #f /etc/passwd", "'/etc/passwd' must be a path inside the journey's folder, relative to it.")]
	[Arguments("scroll sideways", "usage: scroll [up|down|left|right] [pixels] [-s <selector>]")]
	[Arguments("keyboard press a", "usage: keyboard type|inserttext <text>")]
	[Arguments("drag #a", "usage: drag <source> <target>")]
	[Arguments("set media sepia", "usage: set media dark|light")]
	[Arguments("snapshot --delta", "snapshot --delta is not supported in journeys: a checkpoint records the whole tree, and the transcript diff shows what changed.")]
	[Arguments("snapshot -x", "snapshot -x is not supported in journeys; use -i, -c, -u, -d <n> or -s <selector>.")]
	[Arguments("snapshot -d", "-d needs a number")]
	[Arguments("snapshot -s @e3", "'@e3' is an agent-browser ref, which changes from run to run; use find role <role> --name <name>, find text, or a CSS selector.")]
	[Arguments("get cdp-url", "get cdp-url is not supported in journeys: get text|html|value|count|box|styles <selector> | get attr <selector> <name> | get title | get url")]
	[Arguments("get text", "usage: get text|html|value|count|box|styles <selector> | get attr <selector> <name> | get title | get url")]
	[Arguments("is hidden #a", "usage: is visible|enabled|checked <selector>")]
	[Arguments("back now", "usage: back")]
	[Arguments("console all", "usage: console [--clear]")]
	[Arguments("network har stop ../x.har", "'../x.har' must be a path inside the artifacts folder, relative to it.")]
	[Arguments("network har stop", "network har stop needs a path for the HAR file, relative to the artifacts folder")]
	[Arguments("network route **/api --abort", "network route is not supported in journeys: it changes or reads what the page receives outside the page.")]
	[Arguments("network requests --status 2x", "usage: network requests [--clear] [--filter <url>] [--type <types>] [--method <method>] [--status <code>] | network har start | network har stop <path>")]
	[Arguments("vitals --budget lcp", "usage: vitals [--budget <name>=<limit>...]")]
	[Arguments("vitals --budget", "usage: vitals [--budget <name>=<limit>...]")]
	[Arguments("vitals lcp=1", "usage: vitals [--budget <name>=<limit>...]")]
	[Arguments("vitals --budget speed=1", "vitals has no 'speed'; budget one of: ttfb, fcp, lcp, cls, inp")]
	[Arguments("timings --budget lcp=1", "timings has no 'lcp'; budget one of: ttfb, first-paint, fcp, dom-interactive, dom-content-loaded, load")]
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
	public void ExportKeepsVitalsWithoutBudgetsAndSkipsOurOwnReadings() =>
		Journey.Parse("open https://x.test/\ntimings\nvitals --budget lcp=1\nmetrics\nget title").ToAgentBrowserBatch()
			.Should().Be("[[\"open\",\"https://x.test/\"],[\"vitals\"],[\"get\",\"title\"]]\n");

	[Test]
	public void RelativeUrlsNeedABaseUrl()
	{
		var journey = Journey.Parse("click #a\nopen docs", "x.journey");
		var act = () => journey.ToAgentBrowserBatch();
		act.Should().Throw<JourneyParseException>().WithMessage("x.journey:2: 'docs' is relative: give a base URL");
	}
}
