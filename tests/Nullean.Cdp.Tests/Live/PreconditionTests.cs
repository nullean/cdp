using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class PreconditionTests(ChromeFixture chrome)
{
	private static JourneyOptions Options(VersionServer server, TimeSpan? navigation = null) =>
		new() { BaseUrl = new Uri(server.Url), NavigationTimeout = navigation };

	[Test]
	public async Task AHeaderThatChangesLateIsWaitedFor()
	{
		_ = chrome.Browser; // skips without a Chrome
		await using var server = new VersionServer(n => n >= 3 ? "1.2.0" : "1.1.0");
		var precondition = new JourneyPrecondition
		{
			Path = "/",
			Header = "x-build",
			Expected = "1.2.0",
			Timeout = TimeSpan.FromSeconds(20),
			Interval = TimeSpan.FromMilliseconds(100)
		};

		var result = await JourneyPreconditions.WaitAsync(chrome.Browser, precondition, Options(server));

		result.Passed.Should().BeTrue(result.Message);
		result.Attempts.Should().Be(3);
		result.Message.Should().Be("The x-build response header of / is \"1.2.0\".");
	}

	[Test]
	public async Task AnElementAttributeThatChangesLateIsWaitedFor()
	{
		_ = chrome.Browser;
		await using var server = new VersionServer(n => n >= 2 ? "1.2.0" : "1.1.0");
		var precondition = new JourneyPrecondition
		{
			Path = "/",
			Selector = "meta[name='docs:build']",
			Attribute = "content",
			Expected = "1.2.0",
			Timeout = TimeSpan.FromSeconds(20),
			Interval = TimeSpan.FromMilliseconds(100),
		};

		var result = await JourneyPreconditions.WaitAsync(chrome.Browser, precondition, Options(server));

		result.Passed.Should().BeTrue(result.Message);
		result.Attempts.Should().Be(2);
	}

	[Test]
	public async Task ElementTextAndRegexAndContainsAreMatched()
	{
		_ = chrome.Browser;
		await using var server = new VersionServer(_ => "1.74.1-canary.0.3");
		var text = new JourneyPrecondition { Path = "/", Selector = "#build", Expected = "1.74.1-canary.0.3" };
		var regex = new JourneyPrecondition { Path = "/", Header = "X-Build", Expected = @"^1\.74\.\d+-canary", Match = TextMatch.Regex };
		var contains = new JourneyPrecondition { Path = "/", Header = "x-build", Expected = "canary.0", Match = TextMatch.Contains };

		(await JourneyPreconditions.WaitAsync(chrome.Browser, text, Options(server))).Passed.Should().BeTrue();
		(await JourneyPreconditions.WaitAsync(chrome.Browser, regex, Options(server))).Passed.Should().BeTrue();
		(await JourneyPreconditions.WaitAsync(chrome.Browser, contains, Options(server))).Passed.Should().BeTrue();
	}

	[Test]
	public async Task AValueThatNeverArrivesFailsWithWhatWasExpectedAndLastSeen()
	{
		_ = chrome.Browser;
		await using var server = new VersionServer(_ => "1.1.0");
		var precondition = new JourneyPrecondition
		{
			Path = "/",
			Header = "x-build",
			Expected = "1.2.0",
			Timeout = TimeSpan.FromSeconds(1),
			Interval = TimeSpan.FromMilliseconds(200)
		};

		var result = await JourneyPreconditions.WaitAsync(chrome.Browser, precondition, Options(server));

		result.Passed.Should().BeFalse();
		result
			.Message
			.Should()
			.StartWith("Timed out after ")
			.And
			.Contain("waiting for the x-build response header of / to equal \"1.2.0\": last saw \"1.1.0\" in ");
		result.Attempts.Should().BeGreaterThanOrEqualTo(1);
	}

	[Test]
	public async Task AMissingHeaderAndAMissingElementAreSeenAsNothing()
	{
		_ = chrome.Browser;
		await using var server = new VersionServer(_ => "1.1.0");
		var header = new JourneyPrecondition
		{
			Path = "/",
			Header = "x-nothing",
			Expected = "x",
			Timeout = TimeSpan.FromMilliseconds(400),
			Interval = TimeSpan.FromMilliseconds(100)
		};
		var element = new JourneyPrecondition
		{
			Path = "/",
			Selector = "#nothing",
			Expected = "x",
			Timeout = TimeSpan.FromMilliseconds(400),
			Interval = TimeSpan.FromMilliseconds(100)
		};

		(await JourneyPreconditions.WaitAsync(chrome.Browser, header, Options(server))).Message.Should().Contain("last saw nothing");
		(await JourneyPreconditions.WaitAsync(chrome.Browser, element, Options(server))).Message.Should().Contain("last saw nothing");
	}

	[Test]
	public async Task LooksThatFailAreRetriedAndGiveUpAfterTheRetries()
	{
		_ = chrome.Browser;
		// Until it starts, the port refuses connections: a look fails at once, whatever the machine's speed. How many looks failed before
		// it came up depends on how fast the machine starts a context, so only the outcome is asserted for that server.
		await using var comingUp = new VersionServer(_ => "1.2.0", startAfter: TimeSpan.FromMilliseconds(800));
		await using var down = new VersionServer(_ => "1.2.0", startAfter: Timeout.InfiniteTimeSpan);
		var precondition = new JourneyPrecondition
		{
			Path = "/",
			Header = "x-build",
			Expected = "1.2.0",
			Timeout = TimeSpan.FromSeconds(30),
			Interval = TimeSpan.FromMilliseconds(300),
			Retries = 5,
		};

		var recovered = await JourneyPreconditions.WaitAsync(chrome.Browser, precondition, Options(comingUp));
		var gaveUp = await JourneyPreconditions.WaitAsync(chrome.Browser, precondition with { Retries = 2 }, Options(down));

		recovered.Passed.Should().BeTrue(recovered.Message);
		gaveUp.Passed.Should().BeFalse();
		gaveUp.Message.Should().Contain("3 looks failed, the last with:").And.Contain("ERR_CONNECTION_REFUSED");
		gaveUp.Attempts.Should().Be(3);
	}

	[Test]
	public async Task AHostOutsideTheAllowListIsNotLookedAt()
	{
		_ = chrome.Browser;
		await using var server = new VersionServer(_ => "1.2.0");
		var precondition = new JourneyPrecondition { Path = "http://other.invalid/", Header = "x-build", Expected = "1.2.0" };

		var result = await JourneyPreconditions.WaitAsync(chrome.Browser, precondition, Options(server));

		result.Passed.Should().BeFalse();
		result.Attempts.Should().Be(0);
		result.Message.Should().Contain("other.invalid is not in the journey's allow list");
		server.Requests.Should().Be(0);
	}

	[Test]
	public async Task ABadRegexOrASourceThatIsNeitherHeaderNorElementIsRejectedUpFront()
	{
		_ = chrome.Browser;
		await using var server = new VersionServer(_ => "1.2.0");

		var badRegex =
			() => JourneyPreconditions.WaitAsync(
				chrome.Browser,
				new JourneyPrecondition { Path = "/", Header = "x", Expected = "(", Match = TextMatch.Regex },
				Options(server)
			);
		var neither = () => JourneyPreconditions.WaitAsync(chrome.Browser, new JourneyPrecondition { Path = "/", Expected = "x" }, Options(server));
		var both =
			() => JourneyPreconditions.WaitAsync(
				chrome.Browser,
				new JourneyPrecondition { Path = "/", Header = "x", Selector = "h1", Expected = "x" },
				Options(server)
			);

		await badRegex.Should().ThrowAsync<ArgumentException>().WithMessage("'(' is not a valid regular expression*");
		await neither.Should().ThrowAsync<ArgumentException>().WithMessage("A precondition looks at a response header or at an element*");
		await both.Should().ThrowAsync<ArgumentException>();
	}

	[Test]
	public async Task ThePageKnowsItsDocumentResponse()
	{
		_ = chrome.Browser;
		await using var server = new VersionServer(_ => "9.9.9");
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		page.Response.Should().BeNull();

		await page.GotoAsync(server.Url);

		page.Response.Should().NotBeNull();
		page.Response!.Status.Should().Be(200);
		page.Response.Header("X-Build").Should().Be("9.9.9");
		page.Response.Header("x-nothing").Should().BeNull();
	}
}
