using System.Globalization;
using System.Text.Json;

namespace Nullean.Cdp.Accessibility;

/// <summary>One node of the accessibility tree, as printed.</summary>
/// <param name="Role">Chrome's role, e.g. <c>link</c>, <c>heading</c>, <c>StaticText</c>.</param>
/// <param name="Name">The accessible name; empty when there is none.</param>
/// <param name="Value">The value (a text box's text, a combobox's choice); empty when there is none.</param>
/// <param name="Properties">The printed states, in print order: <c>level</c>, <c>checked</c>, <c>pressed</c>,
/// <c>expanded</c>, <c>selected</c>, <c>disabled</c>, <c>required</c>, <c>url</c> (printed only when asked).</param>
/// <param name="Children">The children, after lifting.</param>
public sealed record AxNode(string Role, string Name, string Value, IReadOnlyList<AxProperty> Properties, IReadOnlyList<AxNode> Children);

/// <summary>A state of an accessibility node, its value as invariant text (<c>true</c>, <c>mixed</c>, <c>2</c>).</summary>
public readonly record struct AxProperty(string Name, string Value);

/// <summary>What <see cref="Page.SnapshotAsync"/> prints.</summary>
public sealed record AxSnapshotOptions
{
	/// <summary>The whole tree.</summary>
	public static AxSnapshotOptions Default { get; } = new();

	/// <summary>Drop list markers, line breaks and empty structural nodes (agent-browser's <c>-c</c>).</summary>
	public bool Compact { get; init; }

	/// <summary>Only interactive nodes, headings and named landmarks (agent-browser's <c>-i</c>).</summary>
	public bool Interactive { get; init; }

	/// <summary>Print this many levels below the top; <c>0</c> prints the top level only (agent-browser's <c>-d</c>).</summary>
	public int? Depth { get; init; }

	/// <summary>Print links' targets as <c>url=…</c> (agent-browser's <c>-u</c>).</summary>
	public bool Urls { get; init; }

	/// <summary>Only the tree of the first element this CSS selector matches (agent-browser's <c>-s</c>).</summary>
	public string? Selector { get; init; }
}

/// <summary>Builds <see cref="AxNode"/>s from <c>Accessibility.getFullAXTree</c>.</summary>
public static class AxTree
{
	private static readonly string[] PrintedProperties = ["level", "checked", "pressed", "expanded", "selected", "disabled", "required", "url"];

	private sealed record Raw(bool Ignored, string Role, string Name, string Value, AxProperty[] Properties, string[] ChildIds, long? BackendNodeId);

	/// <summary>
	/// Builds the tree from the <c>nodes</c> array. The root is left out and its children returned, unless
	/// <paramref name="rootBackendNodeId"/> names the DOM node to start from, which is then included.
	/// </summary>
	/// <remarks>
	/// Ignored nodes, unnamed <c>generic</c> and <c>none</c> nodes are replaced by their children, inline text boxes
	/// are dropped, and a named node's static text children are dropped since its name already says it.
	/// </remarks>
	public static IReadOnlyList<AxNode> Build(JsonElement nodes, long? rootBackendNodeId = null)
	{
		var byId = new Dictionary<string, Raw>(StringComparer.Ordinal);
		string? rootId = null;
		string? selectedId = null;
		foreach (var node in nodes.EnumerateArray())
		{
			var id = node.GetProperty("nodeId").GetString()!;
			var backend = node.TryGetProperty("backendDOMNodeId", out var b) && b.TryGetInt64(out var bn) ? bn : (long?)null;
			var raw = new Raw(
				node.TryGetProperty("ignored", out var ignored) && ignored.ValueKind == JsonValueKind.True,
				Text(node, "role"),
				Text(node, "name"),
				Text(node, "value"),
				Properties(node),
				node.TryGetProperty("childIds", out var children) ? [.. children.EnumerateArray().Select(c => c.GetString()!)] : [],
				backend);
			byId[id] = raw;
			if (rootId is null && !node.TryGetProperty("parentId", out _))
				rootId = id;
			if (rootBackendNodeId is not null && selectedId is null && backend == rootBackendNodeId)
				selectedId = id;
		}

		if (rootBackendNodeId is not null)
		{
			if (selectedId is null)
				throw new InvalidOperationException("The selected element has no node in the accessibility tree.");
			return [.. Convert(byId, selectedId)];
		}

		return rootId is null ? [] : [.. byId[rootId].ChildIds.SelectMany(c => Convert(byId, c))];
	}

	private static IEnumerable<AxNode> Convert(Dictionary<string, Raw> byId, string id)
	{
		if (!byId.TryGetValue(id, out var raw) || raw.Role == "InlineTextBox")
			return [];
		var children = raw.ChildIds.SelectMany(c => Convert(byId, c));
		if (raw.Ignored || raw.Role == "RootWebArea" || (raw.Role is "generic" or "none" && raw.Name.Length == 0))
			return children;
		AxNode[] kept = raw.Name.Length > 0 ? [.. children.Where(c => c.Role != "StaticText")] : [.. children];
		return [new AxNode(raw.Role, raw.Name, raw.Value, raw.Properties, kept)];
	}

	private static string Text(JsonElement node, string property)
	{
		if (!node.TryGetProperty(property, out var holder) || !holder.TryGetProperty("value", out var value))
			return string.Empty;
		return value.ValueKind switch
		{
			JsonValueKind.String => value.GetString() ?? string.Empty,
			JsonValueKind.Number => value.GetRawText(),
			JsonValueKind.True => "true",
			JsonValueKind.False => "false",
			_ => string.Empty,
		};
	}

	private static AxProperty[] Properties(JsonElement node)
	{
		if (!node.TryGetProperty("properties", out var properties))
			return [];
		var found = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var property in properties.EnumerateArray())
		{
			var name = property.GetProperty("name").GetString()!;
			if (Array.IndexOf(PrintedProperties, name) < 0 || !property.TryGetProperty("value", out var holder) || !holder.TryGetProperty("value", out var value))
				continue;
			found[name] = value.ValueKind switch
			{
				JsonValueKind.True => "true",
				JsonValueKind.False => "false",
				JsonValueKind.Number => value.GetRawText(),
				JsonValueKind.String => value.GetString() ?? string.Empty,
				_ => string.Empty,
			};
		}

		return [.. PrintedProperties.Where(found.ContainsKey).Select(n => new AxProperty(n, found[n]))];
	}

	internal static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
