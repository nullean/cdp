using System.Text.Json;
using AwesomeAssertions;
using Nullean.Cdp.Accessibility;
using Nullean.Snapshots;

namespace Nullean.Cdp.Tests;

/// <summary>Golden output on trees recorded from Chrome (fixtures/*.axtree.json), so it does not move with Chrome.</summary>
public class AxPrinterTests
{
	private static IReadOnlyList<AxNode> Tree(string page, long? root = null)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", page + ".axtree.json")));
		return AxTree.Build(document.RootElement.GetProperty("nodes"), root);
	}

	[Test]
	public void Full() => AxPrinter.Print(Tree("index")).Should().MatchSnapshot();

	[Test]
	public void Compact() => AxPrinter.Print(Tree("index"), new() { Compact = true }).Should().MatchSnapshot();

	[Test]
	public void Interactive() => AxPrinter.Print(Tree("index"), new() { Interactive = true }).Should().MatchSnapshot();

	[Test]
	public void DepthOne() => AxPrinter.Print(Tree("index"), new() { Depth = 1 }).Should().MatchSnapshot();

	[Test]
	public void About() => AxPrinter.Print(Tree("about"), new() { Compact = true }).Should().MatchSnapshot();
}
