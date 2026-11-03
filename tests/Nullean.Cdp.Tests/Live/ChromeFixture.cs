using TUnit.Core.Interfaces;

namespace Nullean.Cdp.Tests.Live;

/// <summary>
/// One Chrome for the whole test session. Without a Chrome the tests that need it are skipped with the locator's
/// description, unless <c>CDP_BROWSER_REQUIRED=1</c> (CI) makes that a failure.
/// </summary>
public sealed class ChromeFixture : IAsyncInitializer, IAsyncDisposable
{
	private Cdp.Browser? _browser;
	private string? _missing;

	public async Task InitializeAsync()
	{
		var location = new ChromeLocator().Locate();
		if (!location.Found)
		{
			_missing = location.Describe();
			return;
		}

		_browser = await Cdp.Browser.LaunchAsync(new BrowserLaunchOptions { ExecutablePath = location.Path });
	}

	public Cdp.Browser Browser
	{
		get
		{
			if (_browser is not null)
				return _browser;
			if (Environment.GetEnvironmentVariable("CDP_BROWSER_REQUIRED") == "1")
				throw new InvalidOperationException("CDP_BROWSER_REQUIRED=1 but " + _missing);
			Skip.Test(_missing ?? "No Chrome.");
			throw new InvalidOperationException("unreachable");
		}
	}

	public static string Site(string page) => new Uri(Path.Combine(AppContext.BaseDirectory, "site", page)).AbsoluteUri;

	public async ValueTask DisposeAsync()
	{
		if (_browser is not null)
			await _browser.DisposeAsync();
	}
}
