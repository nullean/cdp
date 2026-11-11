using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class CheckTests(ChromeFixture chrome)
{
	private static Uri Site => new(new Uri(ChromeFixture.Site("index.html")), "./");

	private async Task<JourneyResult> Run(string journey, Uri baseUrl, string? artifacts = null, bool guard = true) =>
		await JourneyRunner.RunAsync(
			Journey.Parse(journey),
			chrome.Browser,
			new JourneyOptions { BaseUrl = baseUrl, ArtifactsDirectory = artifacts, GuardNetwork = guard }
		);

	private static string LinksPage(SlowServer other) => "/links?other=" + Uri.EscapeDataString(other.Url + "next");

	[Test]
	public async Task LinksChecksSameSiteLinksAndListsTheBrokenOnes()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);
		await using var other = new SlowServer(TimeSpan.Zero);

		var result = await Run($"open {LinksPage(other)}\nlinks\nlinks --all -s nav", new Uri(server.Url));

		result.Failure.Should().BeNull();
		result
			.Checkpoints
			.Select(c => c.Output)
			.Should()
			.Equal(
				"links: 2 checked, 1 broken, 2 on other sites not checked (--external)\n404 /missing-page",
				"links: 2 checked, 1 broken\n200 /next\n404 /missing-page"
			);
	}

	[Test]
	public async Task ExternalLinksAreCheckedOnlyInsideTheAllowList()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);
		await using var other = new SlowServer(TimeSpan.Zero);

		// The other server is on localhost too, which the base URL allows; .invalid is not allowed.
		var guarded = await Run($"open {LinksPage(other)}\nlinks --external -s footer --all", new Uri(server.Url));
		var unguarded = await Run($"open {LinksPage(other)}\nlinks --external -s footer", new Uri(server.Url), guard: false);

		guarded.Checkpoints.Single().Output.Should().Be($"links: 1 checked, 0 broken, 1 outside the allow list\n200 {other.Url}next");
		unguarded.Checkpoints.Single().Output.Should().Be("links: 2 checked, 1 broken\nnet::ERR_NAME_NOT_RESOLVED https://no-such-host.invalid/");
	}

	[Test]
	public async Task StrictFailsOnABrokenLink()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);

		var result = await Run("open /links\nlinks --strict", new Uri(server.Url));

		result.Failure!.Message.Should().Be($"1 broken link(s):\n404 {server.Url}missing-page");
	}

	[Test]
	public async Task FileLinksAreCheckedOnDisk()
	{
		var folder = Directory.CreateTempSubdirectory("cdp-links-");
		try
		{
			File.WriteAllText(
				Path.Combine(folder.FullName, "index.html"),
				"<!doctype html><title>Files</title><link rel=\"icon\" href=\"data:,\"><a href=\"there.html\">There</a> <a href=\"gone.html\">Gone</a>"
			);
			File.WriteAllText(Path.Combine(folder.FullName, "there.html"), "<!doctype html><title>There</title>");

			var result = await Run("open index.html\nlinks --all", new Uri(new Uri(folder.FullName + Path.DirectorySeparatorChar), "index.html"));

			result.Checkpoints.Single().Output.Should().Be("links: 2 checked, 1 broken\nok /there.html\nfile not found /gone.html");
		}
		finally
		{
			folder.Delete(true);
		}
	}

	[Test]
	public async Task FailedListsBrokenRequestsAndStrictFailsOnThem()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);

		var listed = await Run("open /links\nnetwork requests --failed", new Uri(server.Url));
		var strict = await Run("open /links\nnetwork requests --failed --strict", new Uri(server.Url));

		listed.Checkpoints.Single().Output.Should().Be($"GET {server.Url}missing.png (Image) 404");
		strict.Failure!.Message.Should().Be($"1 request(s) listed with --strict:\nGET {server.Url}missing.png (Image) 404");
	}

	[Test]
	public async Task ScreenshotsTheViewportThePageAndAnElement()
	{
		var artifacts = Directory.CreateTempSubdirectory("cdp-shots-");
		try
		{
			var result = await Run(
				"open shot.html\nset viewport 1280 720\nscreenshot view.png\nscreenshot shots/full.png --full\nscreenshot #box shots/box.png",
				Site,
				artifacts.FullName
			);

			result.Failure.Should().BeNull();
			result.Checkpoints.Should().BeEmpty("screenshots are not checkpoints");
			var view = new Png(File.ReadAllBytes(Path.Combine(artifacts.FullName, "view.png")));
			(view.Width, view.Height).Should().Be((1280, 720));
			view[10, 10].Should().Be(((byte)0, (byte)0, (byte)255), "the heading is at the top");

			var full = new Png(File.ReadAllBytes(Path.Combine(artifacts.FullName, "shots", "full.png")));
			(full.Width, full.Height).Should().Be((1280, 40 + 1500 + 80 + 400));
			full[100, 1580].Should().Be(((byte)255, (byte)0, (byte)0), "the box is below the fold");

			var box = new Png(File.ReadAllBytes(Path.Combine(artifacts.FullName, "shots", "box.png")));
			(box.Width, box.Height).Should().Be((120, 80));
			box[0, 0].Should().Be(((byte)255, (byte)0, (byte)0));
			box[119, 79].Should().Be(((byte)255, (byte)0, (byte)0), "the clip is the element's box, wherever the page scrolled to");
		}
		finally
		{
			artifacts.Delete(true);
		}
	}

	[Test]
	public async Task ScreenshotsNeedTheArtifactsFolder()
	{
		var result = await Run("open shot.html\nscreenshot view.png", Site);

		result.Failure!.Message.Should().Be("screenshot needs an artifacts folder to write into (JourneyOptions.ArtifactsDirectory, or --artifacts).");
	}
}
