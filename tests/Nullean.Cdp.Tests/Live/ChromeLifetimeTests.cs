using System.Diagnostics;
using AwesomeAssertions;

namespace Nullean.Cdp.Tests.Live;

[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class ChromeLifetimeTests(ChromeFixture chrome)
{
	// A process that has exited: its id and start time stand for an owner that was killed.
	private static (int Pid, DateTime Started) DeadOwner()
	{
		using var process = Process.Start(new ProcessStartInfo("dotnet", "--version") { RedirectStandardOutput = true })!;
		var started = process.StartTime;
		process.WaitForExit();
		return (process.Id, started);
	}

	[Test]
	public async Task TheProfileRecordsItsOwner()
	{
		_ = chrome.Browser;
		await using var browser = await Cdp.Browser.LaunchAsync();
		using var self = Process.GetCurrentProcess();

		var owner = await File.ReadAllTextAsync(Path.Combine(browser.UserDataDirectory!, ChromeProcesses.OwnerFile));

		owner.Should().StartWith(ChromeProcesses.Owner(self.Id, self.StartTime)).And.Contain("\nchrome=").And.Contain("\nchromeStarted=");
	}

	[Test]
	public async Task AnOrphansChromeIsClosedAndItsProfileDeleted()
	{
		_ = chrome.Browser;
		var browser = await Cdp.Browser.LaunchAsync();
		var profile = browser.UserDataDirectory!;
		var (pid, started) = DeadOwner();
		var ownerFile = Path.Combine(profile, ChromeProcesses.OwnerFile);
		var chromeLines = string.Concat((await File.ReadAllLinesAsync(ownerFile)).Where(l => l.StartsWith("chrome", StringComparison.Ordinal)).Select(l => l + "\n"));
		await File.WriteAllTextAsync(ownerFile, ChromeProcesses.Owner(pid, started) + chromeLines);

		var reaped = await ChromeProcesses.ReapOrphansAsync(Path.GetDirectoryName(profile));

		reaped.Should().BeGreaterThanOrEqualTo(1);
		Directory.Exists(profile).Should().BeFalse();
		var closed = Stopwatch.StartNew();
		while (!browser.Connection.IsClosed && closed.Elapsed < TimeSpan.FromSeconds(10))
			await Task.Delay(50);
		browser.Connection.IsClosed.Should().BeTrue("the orphan's Chrome was closed through its own DevTools port");
		await browser.DisposeAsync();
	}

	[Test]
	public async Task LiveOwnersAndUnmarkedProfilesAreLeftAlone()
	{
		_ = chrome.Browser;
		await using var browser = await Cdp.Browser.LaunchAsync();
		var unmarked = Directory.CreateTempSubdirectory(ChromeProcesses.ProfilePrefix).FullName;
		try
		{
			await ChromeProcesses.ReapOrphansAsync(Path.GetDirectoryName(browser.UserDataDirectory!));

			Directory.Exists(browser.UserDataDirectory).Should().BeTrue();
			(await browser.VersionAsync()).Should().NotBeEmpty();
			Directory.Exists(unmarked).Should().BeTrue();
		}
		finally
		{
			Directory.Delete(unmarked, recursive: true);
		}
	}

	[Test]
	public async Task AReusedProcessIdIsNotTheOwner()
	{
		_ = chrome.Browser;
		await using var browser = await Cdp.Browser.LaunchAsync();
		using var self = Process.GetCurrentProcess();
		// This process's id, but a start time an hour off: the owner that wrote it is gone.
		var profile = browser.UserDataDirectory!;
		var ownerFile = Path.Combine(profile, ChromeProcesses.OwnerFile);
		var chromeLines = string.Concat((await File.ReadAllLinesAsync(ownerFile)).Where(l => l.StartsWith("chrome", StringComparison.Ordinal)).Select(l => l + "\n"));
		await File.WriteAllTextAsync(ownerFile, ChromeProcesses.Owner(self.Id, self.StartTime.AddHours(-1)) + chromeLines);

		await ChromeProcesses.ReapOrphansAsync(Path.GetDirectoryName(profile));

		Directory.Exists(profile).Should().BeFalse();
	}
}
