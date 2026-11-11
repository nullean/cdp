using System.Text.Json;
using AwesomeAssertions;
using Nullean.Cdp.Accessibility;

namespace Nullean.Cdp.Tests;

public class AxTreeTests
{
	// A compact way to write getFullAXTree nodes: id, role, name, children, and optional extras.
	private static string Node(
		string id,
		string role,
		string? name = null,
		string[]? children = null,
		string? parent = null,
		bool ignored = false,
		long? backend = null,
		string? value = null,
		string? properties = null
	)
	{
		var parts = new List<string>
		{
			$"\"nodeId\":\"{id}\"",
			$"\"ignored\":{(ignored ? "true" : "false")}",
			$"\"role\":{{\"type\":\"role\",\"value\":\"{role}\"}}",
		};
		if (name is not null)
			parts.Add($"\"name\":{{\"type\":\"computedString\",\"value\":{JsonSerializer.Serialize(name)}}}");
		if (value is not null)
			parts.Add($"\"value\":{{\"type\":\"string\",\"value\":{JsonSerializer.Serialize(value)}}}");
		if (children is not null)
			parts.Add($"\"childIds\":[{string.Join(',', children.Select(c => $"\"{c}\""))}]");
		if (parent is not null)
			parts.Add($"\"parentId\":\"{parent}\"");
		if (backend is not null)
			parts.Add($"\"backendDOMNodeId\":{backend}");
		if (properties is not null)
			parts.Add($"\"properties\":[{properties}]");
		return "{" + string.Join(',', parts) + "}";
	}

	private static string Print(long? root, AxSnapshotOptions? options, params string[] nodes)
	{
		using var document = JsonDocument.Parse("[" + string.Join(',', nodes) + "]");
		return AxPrinter.Print(AxTree.Build(document.RootElement, root), options);
	}

	private static string Print(params string[] nodes) => Print(null, null, nodes);

	private static string Prop(string name, string json) => $"{{\"name\":\"{name}\",\"value\":{{\"type\":\"x\",\"value\":{json}}}}}";

	[Test]
	public void RootIsLeftOutAndIgnoredAndUnnamedGenericNodesAreLifted() =>
		Print(
			Node("1", "RootWebArea", "Page", ["2"]),
			Node("2", "none", children: ["3"], parent: "1", ignored: true),
			Node("3", "generic", children: ["4", "5"], parent: "2"),
			Node("4", "button", "Go", parent: "3"),
			Node("5", "generic", "Named", parent: "3")
		).Should().Be("- button \"Go\"\n- generic \"Named\"\n");

	[Test]
	public void InlineTextBoxesAndTheTextOfNamedNodesAreDropped() =>
		Print(
			Node("1", "RootWebArea", children: ["2", "4"]),
			Node("2", "link", "Home", ["3"], "1"),
			Node("3", "StaticText", "Home", ["9"], "2"),
			Node("9", "InlineTextBox", "Home", parent: "3"),
			Node("4", "paragraph", children: ["5"], parent: "1"),
			Node("5", "StaticText", "Kept", ["6"], "4"),
			Node("6", "InlineTextBox", "Kept", parent: "5")
		).Should().Be("- link \"Home\"\n- paragraph\n  - StaticText \"Kept\"\n");

	[Test]
	public void HiddenSubtreesDisappear() =>
		Print(
			Node("1", "RootWebArea", children: ["2", "4"]),
			Node("2", "generic", children: ["3"], parent: "1", ignored: true),
			Node("3", "StaticText", "Hidden", parent: "2", ignored: true),
			Node("4", "button", "Shown", parent: "1")
		).Should().Be("- button \"Shown\"\n");

	[Test]
	public void StatesPrintInAFixedOrderAndFalseFlagsAreLeftOut() =>
		Print(
			Node("1", "RootWebArea", children: ["2"]),
			Node(
				"2",
				"checkbox",
				"C",
				parent: "1",
				properties: string.Join(
					',',
					Prop("required", "true"),
					Prop("disabled", "false"),
					Prop("checked", "\"mixed\""),
					Prop("focusable", "true"),
					Prop("selected", "true"),
					Prop("level", "3")
				)
			)
		).Should().Be("- checkbox \"C\" [level=3, checked=mixed, selected, required]\n");

	[Test]
	public void ValuePrintsAfterAColonUnlessItRepeatsTheName() =>
		Print(
			Node("1", "RootWebArea", children: ["2", "3"]),
			Node("2", "textbox", "Query", parent: "1", value: "shoes\nred"),
			Node("3", "combobox", "Same", parent: "1", value: "Same")
		).Should().Be("- textbox \"Query\": shoes\\nred\n- combobox \"Same\"\n");

	[Test]
	public void NamesAreEscaped() =>
		Print(Node("1", "RootWebArea", children: ["2"]), Node("2", "button", "Say \"hi\" \\ there\n\tnow", parent: "1"))
			.Should()
			.Be("- button \"Say \\\"hi\\\" \\\\ there\\n\\tnow\"\n");

	[Test]
	public void ASelectedElementIsIncludedItself() =>
		Print(
			42,
			null,
			Node("1", "RootWebArea", children: ["2"]),
			Node("2", "main", children: ["3"], parent: "1", backend: 42),
			Node("3", "heading", "Title", parent: "2", backend: 43)
		).Should().Be("- main\n  - heading \"Title\"\n");

	[Test]
	public void AnIgnoredSelectedElementGivesItsChildren() =>
		Print(
			42,
			null,
			Node("1", "RootWebArea", children: ["2"]),
			Node("2", "generic", children: ["3"], parent: "1", backend: 42, ignored: true),
			Node("3", "button", "Go", parent: "2")
		).Should().Be("- button \"Go\"\n");

	[Test]
	public void ASelectedElementOutsideTheTreeFails()
	{
		var act = () => Print(99, null, Node("1", "RootWebArea", children: []));
		act.Should().Throw<InvalidOperationException>().WithMessage("*no node in the accessibility tree*");
	}

	[Test]
	public void CompactDropsMarkersBreaksAndEmptyStructure() =>
		Print(
			null,
			new AxSnapshotOptions { Compact = true },
			Node("1", "RootWebArea", children: ["2", "5"]),
			Node("2", "listitem", children: ["3", "4"], parent: "1"),
			Node("3", "ListMarker", "• ", parent: "2"),
			Node("4", "LineBreak", "\n", parent: "2"),
			Node("5", "group", children: ["6"], parent: "1"),
			Node("6", "button", "Kept", parent: "5")
		).Should().Be("- group\n  - button \"Kept\"\n");

	[Test]
	public void InteractiveKeepsControlsHeadingsAndNamedLandmarksOnly() =>
		Print(
			null,
			new AxSnapshotOptions { Interactive = true },
			Node("1", "RootWebArea", children: ["2", "3"]),
			Node("2", "main", children: ["4", "5"], parent: "1"),
			Node("3", "navigation", "Site", ["6"], "1"),
			Node("4", "paragraph", children: ["7"], parent: "2"),
			Node("5", "heading", "H", parent: "2"),
			Node("6", "link", "L", parent: "3"),
			Node("7", "button", "B", parent: "4")
		).Should().Be("- button \"B\"\n- heading \"H\"\n- navigation \"Site\"\n  - link \"L\"\n");

	[Test]
	public void DepthZeroPrintsTheTopLevelOnly() =>
		Print(
			null,
			new AxSnapshotOptions { Depth = 0 },
			Node("1", "RootWebArea", children: ["2"]),
			Node("2", "main", children: ["3"], parent: "1"),
			Node("3", "button", "B", parent: "2")
		).Should().Be("- main\n");
}
