using System.Collections.Frozen;
using System.Text;

namespace Nullean.Cdp.Accessibility;

/// <summary>
/// Prints an accessibility tree the way agent-browser's <c>snapshot</c> does, without refs:
/// <c>- role "name" [level=1, checked=true, selected]: value</c>, two spaces per level, <c>\n</c> line endings.
/// </summary>
public static class AxPrinter
{
	private static readonly FrozenSet<string> InteractiveRoles = FrozenSet.Create(StringComparer.Ordinal,
		"button", "checkbox", "combobox", "link", "listbox", "menuitem", "menuitemcheckbox", "menuitemradio", "option", "radio",
		"searchbox", "slider", "spinbutton", "switch", "tab", "textbox", "treeitem", "heading");

	private static readonly FrozenSet<string> Landmarks = FrozenSet.Create(StringComparer.Ordinal,
		"banner", "complementary", "contentinfo", "form", "main", "navigation", "region", "search");

	// States printed bare when true and left out otherwise; the rest print as name=value.
	private static readonly FrozenSet<string> Flags = FrozenSet.Create(StringComparer.Ordinal, "selected", "disabled", "required");

	/// <summary>Filters <paramref name="nodes"/> by <paramref name="options"/> and prints them.</summary>
	public static string Print(IReadOnlyList<AxNode> nodes, AxSnapshotOptions? options = null)
	{
		options ??= AxSnapshotOptions.Default;
		IEnumerable<AxNode> filtered = nodes;
		if (options.Interactive)
			filtered = filtered.SelectMany(KeepInteractive);
		if (options.Compact)
			filtered = filtered.SelectMany(Compact);

		var text = new StringBuilder();
		foreach (var node in filtered)
			Append(text, node, 0, options.Depth);
		return text.ToString();
	}

	private static IEnumerable<AxNode> KeepInteractive(AxNode node)
	{
		var children = node.Children.SelectMany(KeepInteractive).ToArray();
		var keep = InteractiveRoles.Contains(node.Role) || (Landmarks.Contains(node.Role) && node.Name.Length > 0);
		return keep ? [node with { Children = children }] : children;
	}

	private static IEnumerable<AxNode> Compact(AxNode node)
	{
		if (node.Role is "ListMarker" or "LineBreak")
			return [];
		var children = node.Children.SelectMany(Compact).ToArray();
		var empty = node.Name.Length == 0 && node.Value.Length == 0 && node.Properties.Count == 0 && children.Length == 0;
		return empty ? [] : [node with { Children = children }];
	}

	private static void Append(StringBuilder text, AxNode node, int depth, int? maxDepth)
	{
		text.Append(' ', depth * 2).Append("- ").Append(node.Role);
		if (node.Name.Length > 0)
			AppendQuoted(text.Append(' '), node.Name);

		var first = true;
		foreach (var property in node.Properties)
		{
			if (Flags.Contains(property.Name) && property.Value != "true")
				continue;
			text.Append(first ? " [" : ", ").Append(property.Name);
			if (!Flags.Contains(property.Name))
				text.Append('=').Append(property.Value);
			first = false;
		}

		if (!first)
			text.Append(']');
		if (node.Value.Length > 0 && node.Value != node.Name)
			text.Append(": ").Append(OneLine(node.Value));
		text.Append('\n');

		if (maxDepth is { } max && depth >= max)
			return;
		foreach (var child in node.Children)
			Append(text, child, depth + 1, maxDepth);
	}

	private static void AppendQuoted(StringBuilder text, string value)
	{
		text.Append('"');
		foreach (var c in value)
		{
			_ = c switch
			{
				'"' => text.Append("\\\""),
				'\\' => text.Append("\\\\"),
				'\n' => text.Append("\\n"),
				'\r' => text.Append("\\r"),
				'\t' => text.Append("\\t"),
				_ => text.Append(c),
			};
		}

		text.Append('"');
	}

	private static string OneLine(string value) => value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
