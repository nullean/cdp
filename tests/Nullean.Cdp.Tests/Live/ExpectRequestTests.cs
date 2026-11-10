using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

// `expect request` and `expect same-document`: assertions about the page's network and documents, never checkpoints.
[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class ExpectRequestTests(ChromeFixture chrome)
{
	private async Task<JourneyResult> RunAsync(string journey)
	{
		_ = chrome.Browser; // skips without a Chrome
		await using var server = new SlowServer(TimeSpan.Zero);
		return await JourneyRunner.RunAsync(Journey.Parse(journey, "x.journey"), chrome.Browser, new JourneyOptions { BaseUrl = new Uri(server.Url) });
	}

	[Test]
	public async Task PassingRequestAssertionsRecordNothing()
	{
		var result = await RunAsync("""
			open /telemetry
			expect request "**/collect/l" --method POST --status 2xx --type Fetch
			expect request "**/collect/l" --body-contains '"docs-frontend"'
			expect request "**/collect/l" --body-contains browser.navigation_timing
			expect request "**/collect/l" --body-regex '"service\.name".{0,30}docs-frontend'
			expect --not request "**/collect/l" --body-contains browser.resource_timing
			expect --not request "**/collect/l" --larger-than 1kb
			expect request "**/collect/big" --larger-than 64kb
			expect request "**/collect/deny" --status 403 --failed
			expect --timeout 5000 request "**/collect/slow" --status 200
			expect --not request "**/collect/l" --failed
			get title
			""");

		result.Failure.Should().BeNull();
		result.Checkpoints.Select(c => c.Command).Should().Equal("get title");
	}

	[Test]
	public async Task ABodyFilterThatDoesNotHoldFailsWithTheRequestsTheUrlMatched()
	{
		var result = await RunAsync("""
			open /telemetry
			expect --timeout 5000 request "**/collect/l" --status 2xx
			expect --timeout 300 request "**/collect/l" --body-contains missing-text
			""");

		result.Failure.Should().NotBeNull();
		result.Failure!.Line.Should().Be(3);
		result.Failure.Message.Should().StartWith("Timed out after 0.3s waiting for the requests to include a request matching \"**/collect/l\" --body-contains \"missing-text\": received 1 with that URL, none matching the other filters");
		result.Failure.Message.Should().Contain("POST http://localhost:").And.Contain("/collect/l (Fetch) 200");
		// The body itself is never printed.
		result.Failure.Message.Should().NotContain("docs-frontend");
	}

	[Test]
	public async Task ARequestNothingMadeSaysHowManyThereWere()
	{
		var result = await RunAsync("open /telemetry\nexpect --timeout 5000 request \"**/collect/l\"\nexpect --timeout 300 request \"**/collect/nothing\"");

		result.Failure!.Message.Should().Contain("received no request with that URL among the ");
	}

	[Test]
	public async Task ANegatedAssertionNamesTheRequestItFound()
	{
		var result = await RunAsync("""
			open /telemetry
			expect --timeout 5000 request "**/collect/slow"
			expect --timeout 300 --not request "**/collect/*" --failed
			""");

		result.Failure!.Line.Should().Be(3);
		result.Failure.Message.Should().Contain("include no request matching \"**/collect/*\" --failed: received found POST http://localhost:").And.Contain("/collect/deny (Fetch) 403");
	}

	[Test]
	public async Task ANegatedAssertionWaitsForRequestsStillInFlight()
	{
		// /collect/slow answers after 1.5 s: until then a failure could still come, so the absence is not yet known.
		var result = await RunAsync("""
			open /telemetry
			expect --timeout 5000 request "**/collect/l" --status 2xx
			expect --timeout 300 --not request "**/collect/slow" --failed
			""");

		result.Failure!.Message.Should().Contain("received 1 still in flight: POST http://localhost:");
	}

	[Test]
	public async Task AMissingRequestBecomesOneWithinTheTimeout()
	{
		var result = await RunAsync("""
			open /telemetry
			expect --timeout 5000 request "**/collect/l" --status 2xx
			expect --timeout 300 request "**/collect/slow" --status 200
			""");

		// The request exists but is not over yet.
		result.Failure!.Message.Should().Contain("(1 still in flight)");
	}

	[Test]
	public async Task ASizeFailureShowsTheBodySizeNotTheBody()
	{
		var result = await RunAsync("""
			open /telemetry
			expect --timeout 5000 request "**/collect/big" --status 2xx
			expect --timeout 300 request "**/collect/big" --larger-than 200kb
			""");

		result.Failure!.Message.Should().Contain("--larger-than 204800 bytes").And.Contain("[100000 bytes]");
	}

	[Test]
	public async Task SameDocumentTellsAnHtmxStyleSwapFromAFullNavigation()
	{
		var result = await RunAsync("""
			open /spa
			expect same-document
			click #push
			expect url "**/spa/inner"
			expect same-document
			expect --timeout 300 --not same-document
			""");

		result.Failure!.Line.Should().Be(6);
		result.Failure.Message.Should().Be("Timed out after 0.3s waiting for the page to have been replaced by a new document since the last action: received the same document.");

		var full = await RunAsync("""
			open /spa
			click #full
			expect --not same-document
			expect --timeout 300 same-document
			""");

		full.Failure!.Line.Should().Be(4);
		full.Failure.Message.Should().Contain("received 1 new document(s) since the last action");
	}
}
