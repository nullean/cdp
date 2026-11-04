using AwesomeAssertions;
using Nullean.Cdp.Accessibility;
using Nullean.Cdp.Journeys;
using Nullean.Snapshots;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class AuditTests(ChromeFixture chrome)
{
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	private async Task<IReadOnlyList<AuditViolation>> Audit(string file, string? selector = null)
	{
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await page.GotoAsync(ChromeFixture.Site(file));
		return await page.AuditAsync(selector);
	}

	[Test]
	public async Task FindsTheCommonProblems() =>
		AccessibilityAudit.Format(await Audit("a11y.html")).Should().MatchSnapshot();

	[Test]
	public async Task ACleanPageHasNone() =>
		AccessibilityAudit.Format(await Audit("about.html")).Should().Be("violations: 0");

	[Test]
	public async Task DisabledControlsAreExemptFromContrast() =>
		(await Audit("index.html")).Should().NotContain(v => v.Rule == "color-contrast", "the low-contrast button is disabled");

	[Test]
	public async Task ASelectorLimitsTheAuditToItsElement() =>
		(await Audit("a11y.html", "a[href='/x']")).Select(v => v.Rule).Should().Equal("link-name");

	[Test]
	public async Task ContrastFollowsWcagAa()
	{
		var contrast = (await Audit("contrast.html")).Single(v => v.Rule == "color-contrast");

		contrast.Nodes.Should().Equal("p.fail (4.48:1)", "p.small-same (3.03:1)", "p.veil (3.98:1)");
	}

	[Test]
	public async Task StrictFailsTheStep()
	{
		var result = await JourneyRunner.RunAsync(Journey.Parse("open a11y.html\na11y --strict"), chrome.Browser, new JourneyOptions { BaseUrl = Site });

		result.Failure!.Message.Should().StartWith("Accessibility violations: aria-hidden-focus (1), button-name (1), color-contrast (1),");
	}
}
