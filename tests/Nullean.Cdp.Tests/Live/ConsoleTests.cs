using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class ConsoleTests(ChromeFixture chrome)
{
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	[Test]
	public async Task ConsoleAndErrorsAreCheckpointsAndClearForgets()
	{
		var journey = Journey.Parse("""
			open console.html
			find role button click --name Go
			wait 300
			console
			errors
			console --clear
			errors --clear
			console
			errors
			""");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions { BaseUrl = Site });

		result.Failure.Should().BeNull();
		var page = Site + "console.html";
		result.Checkpoints.Select(c => c.Output).Should().Equal(
			"[info] loaded\n[log] clicked 1 {a: 2} [1, \"x\"] null undefined\n[warning] careful\n[error] bad thing",
			// Chrome locates errors in inline event handlers within the compiled handler, not the HTML line.
			$"Error: rejected ({page}:3:48)\nError: boom ({page}:2:323)",
			"",
			"");
	}

	[Test]
	public async Task ThePageCollectsFromTheStart()
	{
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(ChromeFixture.Site("console.html"));

		page.ConsoleMessages.Should().ContainSingle().Which.Should().Be(new ConsoleMessage("info", "loaded"));
		// Chrome reports an unhandled rejection a little after the load, not with it.
		var waited = System.Diagnostics.Stopwatch.StartNew();
		while (page.Errors.Count == 0 && waited.Elapsed < TimeSpan.FromSeconds(2))
			await Task.Delay(25);
		page.Errors.Should().ContainSingle().Which.Message.Should().Be("Error: rejected");
	}
}
