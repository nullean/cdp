using System.Runtime.InteropServices;
using AwesomeAssertions;

namespace Nullean.Cdp.Tests;

public class ChromeLocatorTests
{
	private static ChromeLocator Locator(OSPlatform os, Dictionary<string, string> variables, params string[] existing) =>
		new(name => variables.GetValueOrDefault(name), existing.Contains, os);

	[Test]
	public void VariableWinsOverInstallPaths()
	{
		var location = Locator(OSPlatform.Linux, new() { ["CDP_CHROME"] = "/opt/mine/chrome" },
			"/opt/mine/chrome", "/usr/bin/google-chrome").Locate();

		location.Path.Should().Be("/opt/mine/chrome");
		location.Source.Should().Be("CDP_CHROME");
	}

	[Test]
	public void ChromePathIsSecond()
	{
		var location = Locator(OSPlatform.Linux, new() { ["CHROME_PATH"] = "/x/chrome" }, "/x/chrome", "/usr/bin/google-chrome").Locate();

		location.Source.Should().Be("CHROME_PATH");
	}

	[Test]
	public void CdpChromeWinsOverChromePath()
	{
		var location = Locator(OSPlatform.Linux, new() { ["CDP_CHROME"] = "/a/chrome", ["CHROME_PATH"] = "/b/chrome" },
			"/a/chrome", "/b/chrome").Locate();

		location.Path.Should().Be("/a/chrome");
	}

	[Test]
	public void MacInstallPath()
	{
		var location = Locator(OSPlatform.OSX, [], "/Applications/Chromium.app/Contents/MacOS/Chromium").Locate();

		location.Path.Should().Be("/Applications/Chromium.app/Contents/MacOS/Chromium");
		location.Source.Should().Be("install path");
	}

	[Test]
	public void WindowsInstallPathUsesBackslashes()
	{
		var location = Locator(OSPlatform.Windows, new() { ["ProgramFiles"] = @"C:\Program Files" },
			@"C:\Program Files\Google\Chrome\Application\chrome.exe").Locate();

		location.Path.Should().Be(@"C:\Program Files\Google\Chrome\Application\chrome.exe");
	}

	[Test]
	public void FallsBackToPath()
	{
		var location = Locator(OSPlatform.Linux, new() { ["PATH"] = "/home/me/bin:/usr/local/bin" }, "/usr/local/bin/chromium").Locate();

		location.Path.Should().Be("/usr/local/bin/chromium");
		location.Source.Should().Be("PATH");
	}

	[Test]
	public void WindowsPathAddsExe()
	{
		var location = Locator(OSPlatform.Windows, new() { ["PATH"] = @"C:\tools" }, @"C:\tools\chrome.exe").Locate();

		location.Path.Should().Be(@"C:\tools\chrome.exe");
	}

	[Test]
	public void NotFoundListsEverythingTried()
	{
		var location = Locator(OSPlatform.Linux, new() { ["CDP_CHROME"] = "/nope", ["PATH"] = "/bin" }).Locate();

		location.Found.Should().BeFalse();
		var description = location.Describe();
		description.Should().StartWith("No Chrome found. Looked in:\n  /nope   (CDP_CHROME)\n");
		description.Should().Contain("/usr/bin/google-chrome   (install path)");
		description.Should().Contain("/bin/chromium   (PATH)");
		description.Should().EndWith("Set CDP_CHROME to a Chrome, Chromium or Edge executable.");
	}

	[Test]
	public void BlankVariableIsSkipped()
	{
		var location = Locator(OSPlatform.Linux, new() { ["CDP_CHROME"] = "  " }).Locate();

		location.Tried.Should().NotContain(t => t.Source == "CDP_CHROME");
	}
}
