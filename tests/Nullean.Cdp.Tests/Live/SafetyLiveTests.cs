using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class SafetyLiveTests(ChromeFixture chrome)
{
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	private Task<JourneyResult> Run(string journey, Func<string, string?>? secrets = null, bool guard = true) =>
		JourneyRunner.RunAsync(Journey.Parse(journey, "safety.journey"), chrome.Browser, new JourneyOptions
		{
			BaseUrl = Site,
			Secrets = secrets ?? (_ => null),
			GuardNetwork = guard,
			ActionTimeout = TimeSpan.FromSeconds(3),
		});

	[Test]
	public async Task ALinkOffTheAllowListFailsTheStep()
	{
		var result = await Run("open safety.html\nfind role link click --name \"Leave the site\"\nget title");

		result.Failure.Should().Be(new JourneyFailure(2, "find role link click --name \"Leave the site\"",
			"The page navigated to https://leave.example/, which is refused: leave.example is not in the journey's allow list (add: allow leave.example)."));
	}

	[Test]
	public async Task AnAllowLineLetsTheNavigationThrough()
	{
		var result = await Run("allow leave.example\nopen safety.html\nfind role link click --name \"Leave the site\"");

		result.Failure.Should().BeNull("the host is allowed; it does not resolve, which is not a journey failure");
	}

	[Test]
	public async Task APopupOffTheAllowListFailsTheRun()
	{
		var result = await Run("open safety.html\nfind role button click --name \"Open a popup elsewhere\"\nwait 1000\nget title");

		result.Failure.Should().NotBeNull();
		result.Failure!.Line.Should().BeOneOf(2, 3);
		result.Failure.Message.Should().Be(
			"The page navigated to https://leave.example/popup, which is refused: leave.example is not in the journey's allow list (add: allow leave.example).");
	}

	[Test]
	public async Task APopupOnTheAllowListIsFine()
	{
		var result = await Run("open safety.html\nfind role button click --name \"Open a popup here\"\nwait 500\nget title");

		result.Failure.Should().BeNull();
	}

	[Test]
	public async Task RefusedSubResourcesAndFramesAreReportedNotFailed()
	{
		var result = await Run("open safety.html\nwait 300\nget title");

		result.Failure.Should().BeNull();
		result.Blocked.Should().Contain(new BlockedRequest("https://tracker.example/pixel.png", "Image", true));
		result.Blocked.Should().Contain(r => r.Url == "https://ads.example/frame.html" && r.ResourceType == "Document" && !r.TopLevel);
	}

	[Test]
	public async Task OpeningAFileOutsideTheBaseFolderIsRefused()
	{
		var result = await Run("open ../Nullean.Cdp.Tests.dll");

		result.Failure!.Message.Should().StartWith("Opening file:///").And.Contain("Nullean.Cdp.Tests.dll is refused: file URLs are only allowed under ");
	}

	[Test]
	public async Task WithoutTheGuardAnythingGoes()
	{
		var result = await Run("open safety.html\nfind role link click --name \"Leave the site\"", guard: false);

		result.Failure.Should().BeNull();
		result.Blocked.Should().BeEmpty();
	}

	[Test]
	public async Task SecretsAreTypedAndRedactedFromEveryOutput()
	{
		var result = await Run("open safety.html\nfill #user $env:SHOP_USER\nget value #user\nsnapshot -s main -c\nfill #pw $env:SHOP_PASSWORD\nget value #pw",
			name => name switch { "SHOP_USER" => "grace-hopper", "SHOP_PASSWORD" => "hunter22", _ => null });

		result.Failure.Should().BeNull();
		result.Checkpoints[0].Output.Should().Be("$env:SHOP_USER");
		result.Checkpoints[1].Output.Should().Contain("textbox \"User\": $env:SHOP_USER").And.NotContain("grace-hopper");
		result.Checkpoints[2].Output.Should().Be("$env:SHOP_PASSWORD");
	}

	[Test]
	public async Task APasswordFieldRefusesALiteral()
	{
		var result = await Run("open safety.html\nfill #pw hunter22");

		result.Failure.Should().Be(new JourneyFailure(2, "fill #pw hunter22",
			"css #pw is a password field: fill it with a secret reference ($env:NAME), not a literal."));
	}

	[Test]
	public async Task AMissingSecretFailsItsStep()
	{
		var result = await Run("open safety.html\nfill #user $env:NOT_SET");

		result.Failure!.Message.Should().Be("The secret NOT_SET is not set.");
	}

	[Test]
	public async Task DownloadsAreDenied()
	{
		await using var context = await chrome.Browser.NewContextAsync();
		var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var _ = chrome.Browser.Connection.On(null, e =>
		{
			if (e.Method == "Browser.downloadProgress" && e.Params.GetProperty("state").GetString() == "canceled")
				canceled.TrySetResult();
			if (e.Method == "Browser.downloadProgress" && e.Params.GetProperty("state").GetString() == "completed")
				canceled.TrySetException(new InvalidOperationException("The download completed."));
		});
		var page = await context.NewPageAsync();
		await page.GotoAsync(ChromeFixture.Site("safety.html"));

		await page.Text("Download hello").ClickAsync();

		await canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
	}
}
