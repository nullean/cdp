using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nullean.Cdp.Tests.Live;

/// <summary>
/// Records the raw <c>Accessibility.getFullAXTree</c> of the fixture pages for the printer's golden tests, so those
/// do not change with the installed Chrome. Run by hand after changing a page:
/// <c>dotnet test --project tests/Nullean.Cdp.Tests --treenode-filter "/*/*/RecordFixtures/*"</c>.
/// </summary>
[ClassDataSource<ChromeFixture>(Shared = SharedType.PerTestSession)]
public class RecordFixtures(ChromeFixture chrome)
{
	[Test, Explicit]
	public async Task Record()
	{
		foreach (var page in new[] { "index", "about" })
		{
			await using var context = await chrome.Browser.NewContextAsync();
			var tab = await context.NewPageAsync();
			await tab.GotoAsync(ChromeFixture.Site(page + ".html"));
			var tree = await tab.SendAsync("Accessibility.getFullAXTree");
			var json = JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true });
			await File.WriteAllTextAsync(Path.Combine(Folder(), "fixtures", page + ".axtree.json"), json.Replace("\r\n", "\n") + "\n");
		}
	}

	private static string Folder([CallerFilePath] string file = "") => Path.GetDirectoryName(Path.GetDirectoryName(file))!;
}
