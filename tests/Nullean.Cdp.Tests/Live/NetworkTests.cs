using System.Text.Json;
using AwesomeAssertions;
using Nullean.Cdp.Journeys;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class NetworkTests(ChromeFixture chrome)
{
	[Test]
	public async Task RecordsRequestsWithResponsesAndTimings()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.FromMilliseconds(200));
		await using var context = await chrome.Browser.NewContextAsync();
		var page = await context.NewPageAsync();
		await using var recording = await page.RecordNetworkAsync();

		await page.SetExtraHeadersAsync(new Dictionary<string, string> { ["Authorization"] = "Bearer secret-token" });
		await page.GotoAsync(server.Url);

		recording.Requests.Select(r => r.ToString()).Should().Equal(
			$"GET {server.Url} (Document) 200",
			$"GET {server.Url}slow.png (Image) 200");
		var har = recording.ToHar();
		using var document = JsonDocument.Parse(har);
		var entries = document.RootElement.GetProperty("log").GetProperty("entries");
		entries.GetArrayLength().Should().Be(2);
		var image = entries[1];
		image.GetProperty("response").GetProperty("content").GetProperty("mimeType").GetString().Should().Be("image/png");
		var timings = image.GetProperty("timings");
		timings.GetProperty("wait").GetDouble().Should().BeGreaterThanOrEqualTo(150, "the image answers 200 ms late");
		var phases = new[] { "blocked", "dns", "connect", "send", "wait", "receive" }.Sum(p => Math.Max(0, timings.GetProperty(p).GetDouble()));
		image.GetProperty("time").GetDouble().Should().BeApproximately(phases, 0.01, "time is the sum of the phases");
		har.Should().NotContain("secret-token").And.Contain("[redacted]");
	}

	[Test]
	public async Task JourneysListAndFilterRequests()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);
		var journey = Journey.Parse("""
			open /
			network requests
			network requests --type image --status 2xx
			network requests --filter **/next
			network requests --clear
			network requests
			""");

		var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions { BaseUrl = new Uri(server.Url) });

		result.Failure.Should().BeNull();
		result.Checkpoints.Select(c => c.Output).Should().Equal(
			$"GET {server.Url} (Document) 200\nGET {server.Url}slow.png (Image) 200",
			$"GET {server.Url}slow.png (Image) 200",
			"",
			"");
	}

	[Test]
	public async Task HarFilesGoIntoTheArtifactsFolderWithSecretsRedacted()
	{
		_ = chrome.Browser;
		await using var server = new SlowServer(TimeSpan.Zero);
		var artifacts = Directory.CreateTempSubdirectory("cdp-artifacts-").FullName;
		try
		{
			var journey = Journey.Parse("""
				open /
				network har start
				set headers '{"x-token": "$env:TOKEN"}'
				open /next
				network har stop runs/next.har
				""");
			var result = await JourneyRunner.RunAsync(journey, chrome.Browser, new JourneyOptions
			{
				BaseUrl = new Uri(server.Url),
				ArtifactsDirectory = artifacts,
				Secrets = name => name == "TOKEN" ? "hunter22" : null,
			});

			result.Failure.Should().BeNull();
			var har = await File.ReadAllTextAsync(Path.Combine(artifacts, "runs", "next.har"));
			using var document = JsonDocument.Parse(har);
			document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray()
				.Select(e => e.GetProperty("request").GetProperty("url").GetString()).Should().Equal($"{server.Url}next", $"{server.Url}slow.png");
		}
		finally
		{
			Directory.Delete(artifacts, recursive: true);
		}
	}

	[Test]
	public async Task WithoutAnArtifactsFolderNoHarIsWritten()
	{
		var result = await JourneyRunner.RunAsync(Journey.Parse("network har start\nnetwork har stop x.har"), chrome.Browser);

		result.Failure!.Message.Should().Be("network har stop needs an artifacts folder to write into (JourneyOptions.ArtifactsDirectory, or --artifacts).");
	}
}
