using AwesomeAssertions;

namespace Nullean.Cdp.Tests;

public class BrowserLaunchTests
{
	[Test]
	public void DevToolsActivePortGivesTheBrowsersWebSocket() =>
		Browser.ParseDevToolsActivePort(["53117", "/devtools/browser/0b5c-77e"])!.ToString().Should().Be("ws://127.0.0.1:53117/devtools/browser/0b5c-77e");

	// Lines joined with '|'.
	[Test]
	[Arguments("")]
	[Arguments("53117")]
	[Arguments("|/devtools/browser/x")]
	[Arguments("0|/devtools/browser/x")]
	[Arguments("70000|/devtools/browser/x")]
	[Arguments("-1|/devtools/browser/x")]
	[Arguments(" 53117|/devtools/browser/x")]
	[Arguments("53117|/json/version")]
	public void HalfWrittenOrUnexpectedFilesAreNotYetReady(string lines) =>
		Browser.ParseDevToolsActivePort(lines.Length == 0 ? [] : lines.Split('|')).Should().BeNull();

	[Test]
	public void ArgumentsUseAnOwnProfileAndAnyFreePort()
	{
		var arguments = Browser.Arguments(new BrowserLaunchOptions { Arguments = ["--lang=nl"] }, "/tmp/p").ToArray();

		arguments.Should().StartWith(["--headless=new", "--hide-scrollbars", "--remote-debugging-port=0", "--user-data-dir=/tmp/p"]);
		arguments.Should().EndWith(["--lang=nl", "about:blank"]);
	}

	[Test]
	public void HeadedLeavesOutHeadlessAndKeepsScrollbars() =>
		Browser.Arguments(new BrowserLaunchOptions { Headless = false }, "/tmp/p").Should().NotContain(["--headless=new", "--hide-scrollbars"]);

	[Test]
	public async Task AMissingExecutableIsALaunchError()
	{
		var act = () => Browser.LaunchAsync(new BrowserLaunchOptions { ExecutablePath = Path.Combine(Path.GetTempPath(), "no-such-chrome") });
		await act.Should().ThrowAsync<BrowserLaunchException>().WithMessage("Could not start *no-such-chrome*");
	}

	[Test]
	public async Task AnExecutableThatExitsIsALaunchErrorWithItsOutput()
	{
		if (OperatingSystem.IsWindows())
			Skip.Test("Uses a shell script as the executable.");
		var script = Path.Combine(Path.GetTempPath(), $"fake-chrome-{Guid.NewGuid():N}.sh");
		await File.WriteAllTextAsync(script, "#!/bin/sh\necho 'cannot open display' >&2\nexit 3\n");
		File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
		try
		{
			var act = () => Browser.LaunchAsync(new BrowserLaunchOptions { ExecutablePath = script });
			(await act.Should().ThrowAsync<BrowserLaunchException>())
				.Which
				.Message
				.Should()
				.StartWith("Chrome exited with code 3 before opening its DevTools port.\nLast output:\n  cannot open display");
		}
		finally
		{
			File.Delete(script);
		}
	}
}
