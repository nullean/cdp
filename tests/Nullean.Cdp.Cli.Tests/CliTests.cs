using System.Text.Json;
using AwesomeAssertions;
using Nullean.Snapshots;

namespace Nullean.Cdp.Cli.Tests;

public class CliTests
{
	private const string Nonce = "0123456789abcdef0123456789abcdef";

	private static string Site(params string[] parts) => Path.Combine([AppContext.BaseDirectory, "site", .. parts]);

	private static string SiteUrl => new Uri(Site() + Path.DirectorySeparatorChar).AbsoluteUri;

	private readonly StringWriter _out = new();
	private readonly StringWriter _err = new();
	private static readonly Lazy<Task<Browser>> Shared = new(() => Browser.LaunchAsync());

	private CdpCli Cli(Func<string, string?>? environment = null, bool noBrowser = false) =>
		new(_out, _err,
			noBrowser ? (_, _) => throw new BrowserLaunchException("No Chrome found. Looked in: nowhere") : (_, _) => Borrowed(),
			environment ?? (_ => null), Nonce);

	// The tests share one browser; the command disposes what it is given, so give it a connection of its own.
	private static async Task<Browser> Borrowed()
	{
		// As in the library tests: CI runs the browser tests on Linux only.
		if (Environment.GetEnvironmentVariable("CDP_SKIP_BROWSER") == "1")
			Skip.Test("CDP_SKIP_BROWSER=1: browser tests run on Linux in CI.");
		var shared = await Shared.Value;
		return await Browser.ConnectAsync(new Uri(shared.WebSocketUrl));
	}

	private string Out => _out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).Replace(SiteUrl, "{site}/", StringComparison.Ordinal);

	[Test]
	public void CheckReportsEveryJourney()
	{
		Cli().Check([Site("journeys", "everything.journey")]).Should().Be(ExitCode.Ok);
		_out.ToString().Should().EndWith("everything.journey: 40 steps, 16 checkpoints" + Environment.NewLine);
	}

	[Test]
	public async Task CheckReportsParseErrorsAndExitsTwo()
	{
		var file = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(file, "open /\npdf x.pdf\nclick @e1\n");
		try
		{
			Cli().Check([file]).Should().Be(ExitCode.Usage);
			_err.ToString().Should().Contain($"{file}:2: 'pdf' is not supported").And.Contain($"{file}:3: '@e1' is an agent-browser ref");
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public void BatchPrintsAgentBrowserJsonAndTheDomainsHint()
	{
		Cli().Batch(Site("journeys", "everything.journey"), "http://localhost:5000/", false).Should().Be(ExitCode.Ok);

		using var batch = JsonDocument.Parse(_out.ToString());
		batch.RootElement.GetArrayLength().Should().Be(40);
		batch.RootElement[0].EnumerateArray().Select(e => e.GetString()).Should().Equal("open", "http://localhost:5000/actions.html");
		_err.ToString().Should().Contain("agent-browser --allowed-domains localhost batch --bail");
	}

	[Test]
	[Arguments("everything.journey")]
	[Arguments("more.journey")]
	[Arguments("syntax.journey")]
	public void ParsePrintsTheStepsAsJson(string journey)
	{
		Cli().Parse(Site("journeys", journey)).Should().Be(ExitCode.Ok);

		// "source" is the path as given, which differs per machine; JSON escapes a Windows path's backslashes.
		var folder = Site("journeys") + Path.DirectorySeparatorChar;
		_out.ToString().Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace(folder.Replace("\\", "\\\\", StringComparison.Ordinal), "", StringComparison.Ordinal)
			.Replace(folder, "", StringComparison.Ordinal)
			.Should().MatchSnapshot();
	}

	[Test]
	public void ParseReportsErrorsWithTheirLine()
	{
		var file = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.journey");
		File.WriteAllText(file, "open /\nfrobnicate\n");
		try
		{
			Cli().Parse(file).Should().Be(ExitCode.Usage);
			_out.ToString().Should().BeEmpty();
			_err.ToString().Should().Contain(":2");
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public void ABaseUrlThatIsNeitherUrlNorFolderIsAUsageError()
	{
		Cli().Batch(Site("journeys", "everything.journey"), "no/such/folder", false).Should().Be(ExitCode.Usage);
		_err.ToString().Should().Contain("--base-url 'no/such/folder' is not a URL or an existing folder.");
	}

	[Test]
	public async Task NoChromeExitsThree()
	{
		(await Cli(noBrowser: true).RunAsync([Site("journeys", "everything.journey")], Site(), [], null, false, false, 0, null, default))
			.Should().Be(ExitCode.Browser);
		_err.ToString().Should().StartWith("No Chrome found.");
	}

	[Test]
	public async Task RunPrintsFencedCheckpoints()
	{
		var code = await Cli().RunAsync([Site("journeys", "everything.journey")], Site(), [], null, false, false, 0, null, default);

		code.Should().Be(ExitCode.Ok);
		var text = Out;
		text.Should().StartWith($"# Page content is fenced with nonce={Nonce}.");
		text.Should().Contain($"-- {Site("journeys", "everything.journey")}:7: get text #log\n--- PAGE_CONTENT nonce={Nonce} origin=file: ---\nurl: {{site}}/actions.html\nclicked hello\n--- END_PAGE_CONTENT nonce={Nonce} ---\n");
		text.Should().EndWith("everything.journey: 40 steps, 16 checkpoints\n");
	}

	[Test]
	public async Task AFeatureBlockTheRunDoesNotMeetIsOneLineWithNoFence()
	{
		var file = Path.Combine(Site("journeys"), $"skip-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(file, "open index.html\nfeature preview\n  get title\nend\nget title\n");
		try
		{
			var code = await Cli().RunAsync([file], Site(), [], null, false, false, 0, null, default);

			code.Should().Be(ExitCode.Ok);
			Out.Should().Contain($"-- {file}:2: feature preview (skipped: preview)\n-- {file}:5: get title\n");
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public async Task ARefusedRequestThatRepeatsIsListedOnceWithItsCount()
	{
		var file = Path.Combine(Site("journeys"), $"repeat-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(file, "open safety.html\nopen safety.html\nget title\n");
		try
		{
			await Cli().RunAsync([file], Site(), [], null, false, false, 0, null, default);

			Out.Should().Contain("(x2)").And.Contain("distinct)");
			Out.Split("Image https://tracker.example/pixel.png").Length.Should().Be(2, "the request is named once");
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public async Task AFailureNamesItsScreenshotWhenAskedForWithAnArtifactsFolder()
	{
		var file = Path.Combine(Site("journeys"), $"shot-{Guid.NewGuid():N}.journey");
		var artifacts = Directory.CreateTempSubdirectory("cdp-cli-").FullName;
		await File.WriteAllTextAsync(file, "open index.html\nclick #missing\n");
		try
		{
			var code = await Cli().RunAsync([file], Site(), [], null, false, false, 0, 0.2, default, artifacts: artifacts, screenshotOnFailure: true);

			code.Should().Be(ExitCode.Failed);
			Out.Should().Contain($"-- screenshot: {Path.Combine(artifacts, "failure.png")}\n");
			File.Exists(Path.Combine(artifacts, "failure.png")).Should().BeTrue();
		}
		finally
		{
			File.Delete(file);
			Directory.Delete(artifacts, recursive: true);
		}
	}

	[Test]
	public async Task AFailureIsFencedAndExitsOne()
	{
		var file = Path.Combine(Site("journeys"), $"fail-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(file, "open safety.html\nfind role link click --name \"Leave the site\"\n");
		try
		{
			var code = await Cli().RunAsync([file], Site(), [], null, false, false, 0, null, default);

			code.Should().Be(ExitCode.Failed);
			Out.Should().Contain($"FAILED {file}:2: find role link click --name \"Leave the site\"\n--- PAGE_CONTENT nonce={Nonce} origin=chrome-error: ---\n" +
				"url: chrome-error://chromewebdata/\n" +
				"The page navigated to https://leave.example/, which is refused: leave.example is not in the journey's allow list (add: allow leave.example).\n" +
				$"--- END_PAGE_CONTENT nonce={Nonce} ---\n");
			Out.Should().Contain("request(s) outside the allow list were refused").And.Contain("Image https://tracker.example/pixel.png");
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public async Task AllowLetsTheRunLeave()
	{
		var file = Path.Combine(Site("journeys"), $"leave-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(file, "open safety.html\nfind role link click --name \"Leave the site\"\n");
		try
		{
			(await Cli().RunAsync([file], Site(), ["leave.example"], null, false, false, 0, null, default)).Should().Be(ExitCode.Ok);
			(await Cli().RunAsync([file], Site(), ["https://x"], null, false, false, 0, null, default)).Should().Be(ExitCode.Usage);
		}
		finally
		{
			File.Delete(file);
		}
	}

	[Test]
	public async Task JsonPutsPageContentUnderPage()
	{
		(await Cli().RunAsync([Site("journeys", "everything.journey")], Site(), [], null, false, true, 0, null, default)).Should().Be(ExitCode.Ok);

		using var json = JsonDocument.Parse(_out.ToString());
		var journey = json.RootElement.GetProperty("journeys")[0];
		journey.GetProperty("passed").GetBoolean().Should().BeTrue();
		var first = journey.GetProperty("checkpoints")[0];
		first.GetProperty("line").GetInt32().Should().Be(7);
		first.GetProperty("page").GetProperty("output").GetString().Should().Be("clicked hello");
		json.RootElement.GetProperty("note").GetString().Should().StartWith("Every \"page\" object holds content from the page");
	}

	[Test]
	public async Task OutputIsCutInsideTheFence()
	{
		(await Cli().SnapshotAsync(Site("index.html"), new() { Compact = true }, [], false, 60, default)).Should().Be(ExitCode.Ok);

		Out.Should().Contain("[truncated: showing 60 of").And.EndWith($"--- END_PAGE_CONTENT nonce={Nonce} ---\n");
	}

	[Test]
	public async Task ScreenshotWritesAPng()
	{
		var file = Path.Combine(Path.GetTempPath(), $"shot-{Guid.NewGuid():N}", "box.png");
		try
		{
			(await Cli().ScreenshotAsync(Site("index.html"), file, "h1", false, 800, 600, [], false, default)).Should().Be(ExitCode.Ok);
			var png = await File.ReadAllBytesAsync(file);
			png.AsSpan(1, 3).ToArray().Should().Equal("PNG"u8.ToArray());
			Out.Should().Be($"wrote {file}\n");
		}
		finally
		{
			if (Directory.Exists(Path.GetDirectoryName(file)))
				Directory.Delete(Path.GetDirectoryName(file)!, true);
		}
	}

	[Test]
	public async Task ScreenshotRejectsASelectorWithFull() =>
		(await Cli().ScreenshotAsync(Site("index.html"), "x.png", "h1", true, 800, 600, [], false, default)).Should().Be(ExitCode.Usage);

	[Test]
	public async Task SnapshotPrintsAFencedTree()
	{
		(await Cli().SnapshotAsync(Site("about.html"), new() { Compact = true }, [], false, 0, default)).Should().Be(ExitCode.Ok);

		Out.Should().MatchSnapshot();
	}

	[Test]
	public async Task SecretsComeFromTheEnvironmentAndAreRedacted()
	{
		var file = Path.Combine(Site("journeys"), $"secret-{Guid.NewGuid():N}.journey");
		await File.WriteAllTextAsync(file, "open safety.html\nfill #user $env:CDP_TEST_USER\nget value #user\n");
		try
		{
			var code = await Cli(name => name == "CDP_TEST_USER" ? "grace-hopper" : null).RunAsync([file], Site(), [], null, false, false, 0, null, default);

			code.Should().Be(ExitCode.Ok);
			Out.Should().Contain("$env:CDP_TEST_USER").And.NotContain("grace-hopper");
		}
		finally
		{
			File.Delete(file);
		}
	}
}
