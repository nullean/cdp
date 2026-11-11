using System.Text;
using System.Text.Json;

namespace Nullean.Cdp.Accessibility;

/// <summary>One rule a page breaks, and where.</summary>
/// <param name="Rule">The rule's id, named as axe-core names the same check where there is one: <c>image-alt</c>, …</param>
/// <param name="Impact"><c>critical</c>, <c>serious</c>, <c>moderate</c> or <c>minor</c>.</param>
/// <param name="Help">What the rule asks for.</param>
/// <param name="Nodes">The elements that break it, described by tag and identifying attributes.</param>
public sealed record AuditViolation(string Rule, string Impact, string Help, IReadOnlyList<string> Nodes);

/// <summary>
/// A small accessibility audit of our own, no third-party engine: names and structure from Chrome's accessibility
/// tree, and document, ARIA, id and colour-contrast checks on the DOM. It finds the common problems; it is not a full
/// WCAG audit.
/// </summary>
public static class AccessibilityAudit
{
	private sealed record Rule(string Id, string Impact, string Help);

	private static readonly Rule ImageAlt = new("image-alt", "critical", "Images need alternative text (alt), or alt=\"\" when decorative");
	private static readonly Rule ButtonName = new("button-name", "critical", "Buttons need an accessible name");
	private static readonly Rule Label = new("label", "critical", "Form fields need a label");
	private static readonly Rule LinkName = new("link-name", "serious", "Links need an accessible name");
	private static readonly Rule EmptyHeading = new("empty-heading", "minor", "Headings need text");
	private static readonly Rule HeadingOrder = new("heading-order", "moderate", "Heading levels go down one at a time");
	private static readonly Rule OneMain = new("landmark-one-main", "moderate", "A page has exactly one main landmark");

	private static readonly HashSet<string> FieldRoles =
	[
		with(StringComparer.Ordinal),
		"textbox",
		"searchbox",
		"combobox",
		"listbox",
		"checkbox",
		"radio",
		"slider",
		"spinbutton",
		"switch"
	];

	// Document, ARIA, id and contrast rules, on the DOM. Each finding: [rule, impact, help, node description].
	private const string DomRules =
		"""
		function(scoped) {
			const root = this;
			const describe = e => {
				const tag = e.tagName.toLowerCase();
				if (e.id && document.querySelectorAll('#' + CSS.escape(e.id)).length === 1) return tag + '#' + e.id;
				const attrs = ['name', 'type', 'href', 'src', 'aria-hidden', 'role'].filter(a => e.hasAttribute(a))
					.map(a => '[' + a + '="' + e.getAttribute(a).slice(0, 40) + '"]').slice(0, 2).join('');
				const cls = e.classList.length ? '.' + [...e.classList].slice(0, 2).join('.') : '';
				return tag + cls + attrs;
			};
			const all = [root, ...root.querySelectorAll('*')].filter(e => e.nodeType === 1);
			const found = [];
			if (!scoped) {
				if (!document.title.trim()) found.push(['document-title', 'serious', 'The document needs a non-empty <title>', 'html']);
				if (!document.documentElement.lang.trim()) found.push(['html-has-lang', 'serious', 'The <html> element needs a lang attribute', 'html']);
			}
			const focusable = 'a[href], button:not([disabled]), input:not([disabled]):not([type=hidden]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"]), [contenteditable=""], [contenteditable=true]';
			for (const e of all.filter(e => e.getAttribute('aria-hidden') === 'true'))
				if (e.matches(focusable) || e.querySelector(focusable))
					found.push(['aria-hidden-focus', 'serious', 'Content hidden with aria-hidden must not be focusable', describe(e)]);
			const seen = new Map();
			for (const e of all.filter(e => e.id)) {
				if (seen.has(e.id)) found.push(['duplicate-id', 'minor', 'Ids are unique in the document', e.tagName.toLowerCase() + '#' + e.id]);
				else seen.set(e.id, e);
			}
			const rgba = c => { const m = c.match(/[\d.]+/g)?.map(Number) ?? [0, 0, 0, 0]; return [m[0], m[1], m[2], m.length > 3 ? m[3] : 1]; };
			const over = (top, under) => top.slice(0, 3).map((c, i) => c * top[3] + under[i] * (1 - top[3])).concat(1);
			const luminance = ([r, g, b]) => [r, g, b].map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; })
				.reduce((sum, v, i) => sum + v * [0.2126, 0.7152, 0.0722][i], 0);
			for (const e of all) {
				if (![...e.childNodes].some(n => n.nodeType === 3 && n.textContent.trim())) continue;
				// WCAG exempts inactive controls from contrast.
				if (e.matches(':disabled') || e.closest('[aria-disabled="true"], fieldset:disabled')) continue;
				const style = getComputedStyle(e);
				const box = e.getBoundingClientRect();
				if (box.width === 0 || box.height === 0 || style.visibility === 'hidden' || +style.opacity === 0) continue;
				// The nearest solid background; an image or gradient on the way makes it undecidable, so it is skipped.
				let background = [255, 255, 255, 1], undecidable = false;
				const layers = [];
				for (let a = e; a; a = a.parentElement) {
					const s = getComputedStyle(a);
					if (s.backgroundImage !== 'none') { undecidable = true; break; }
					const c = rgba(s.backgroundColor);
					if (c[3] > 0) { layers.push(c); if (c[3] === 1) break; }
				}
				if (undecidable) continue;
				for (const layer of layers.reverse()) background = over(layer, background);
				const text = over(rgba(style.color), background);
				const [l1, l2] = [luminance(text), luminance(background)].sort((x, y) => y - x);
				const ratio = (l1 + 0.05) / (l2 + 0.05);
				const size = parseFloat(style.fontSize), bold = parseInt(style.fontWeight, 10) >= 700;
				const needed = size >= 24 || (bold && size >= 18.66) ? 3 : 4.5;
				if (ratio < needed)
					found.push(['color-contrast', 'serious', 'Text contrast meets WCAG AA (4.5:1, or 3:1 for large text)', describe(e) + ' (' + ratio.toFixed(2) + ':1)']);
			}
			return found;
		}
		""";

	private const string DescribeFunction =
		"""
		function() {
			const e = this.nodeType === 1 ? this : this.parentElement;
			const tag = e.tagName.toLowerCase();
			if (e.id && document.querySelectorAll('#' + CSS.escape(e.id)).length === 1) return tag + '#' + e.id;
			const attrs = ['name', 'type', 'href', 'src', 'role'].filter(a => e.hasAttribute(a))
				.map(a => '[' + a + '="' + e.getAttribute(a).slice(0, 40) + '"]').slice(0, 2).join('');
			const cls = e.classList.length ? '.' + [...e.classList].slice(0, 2).join('.') : '';
			return tag + cls + attrs;
		}
		""";

	/// <summary>Audits the page, or only the first element <paramref name="selector"/> matches.</summary>
	public static async Task<IReadOnlyList<AuditViolation>> AuditAsync(this Page page, string? selector = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		var findings = new List<(Rule Rule, string Node)>();

		// The accessibility tree, in document order, limited to the selected element's subtree.
		var tree = await page.SendAsync("Accessibility.getFullAXTree", null, cancellationToken).ConfigureAwait(false);
		var nodes = tree.GetProperty("nodes").EnumerateArray().ToList();
		// Chrome lists the text of CSS generated content (counters, ::before) under one synthetic id more than once;
		// the last one wins, as in AxTree.Build.
		var byId = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
		foreach (var node in nodes)
			byId[node.GetProperty("nodeId").GetString()!] = node;
		long? scopeBackend = selector is null ? null : await BackendNodeIdAsync(page, selector, cancellationToken).ConfigureAwait(false);
		var start = scopeBackend is { } b
			? nodes.FirstOrDefault(n => n.TryGetProperty("backendDOMNodeId", out var id) && id.GetInt64() == b)
			: nodes.FirstOrDefault(n => !n.TryGetProperty("parentId", out _));
		var ordered = new List<JsonElement>();
		if (start.ValueKind == JsonValueKind.Object)
			Walk(start, byId, ordered);

		var headings = new List<(int Level, JsonElement Node)>();
		var mains = 0;
		foreach (var node in ordered)
		{
			if (node.TryGetProperty("ignored", out var ignored) && ignored.ValueKind == JsonValueKind.True)
				continue;
			var role = node.TryGetProperty("role", out var r) ? r.StringOrNull("value") : null;
			var name = (node.TryGetProperty("name", out var n) ? n.StringOrNull("value") : null)?.Trim() ?? string.Empty;
			var rule = role switch
			{
				"image" when name.Length == 0 => ImageAlt,
				"button" when name.Length == 0 => ButtonName,
				"link" when name.Length == 0 => LinkName,
				"heading" when name.Length == 0 => EmptyHeading,
				_ when role is not null && FieldRoles.Contains(role) && name.Length == 0 => Label,
				_ => null,
			};
			if (rule is not null)
				findings.Add((rule, await DescribeAsync(page, node, cancellationToken).ConfigureAwait(false)));
			if (role == "heading" && Level(node) is { } level)
				headings.Add((level, node));
			if (role == "main")
				mains++;
		}

		for (var i = 1; i < headings.Count; i++)
		{
			if (headings[i].Level > headings[i - 1].Level + 1)
				findings.Add((HeadingOrder, await DescribeAsync(page, headings[i].Node, cancellationToken).ConfigureAwait(false)));
		}

		if (selector is null && mains != 1)
			findings.Add((OneMain, "html"));

		foreach (var finding in await DomFindingsAsync(page, selector, cancellationToken).ConfigureAwait(false))
			findings.Add(finding);

		return [
			.. findings
				.GroupBy(f => f.Rule.Id, StringComparer.Ordinal)
				.OrderBy(g => g.Key, StringComparer.Ordinal)
				.Select(g => new AuditViolation(g.Key, g.First().Rule.Impact, g.First().Rule.Help, [.. g.Select(f => f.Node)]))
		];
	}

	/// <summary>The audit as agent-browser lays out <c>a11y</c>: a count, then each rule with its elements.</summary>
	public static string Format(IReadOnlyList<AuditViolation> violations)
	{
		ArgumentNullException.ThrowIfNull(violations);
		var text = new StringBuilder().Append("violations: ").Append(violations.Count);
		foreach (var v in violations)
		{
			text
				.Append('\n')
				.Append('[')
				.Append(v.Impact)
				.Append("] ")
				.Append(v.Rule)
				.Append(": ")
				.Append(v.Help)
				.Append(" (")
				.Append(v.Nodes.Count)
				.Append(v.Nodes.Count == 1 ? " node)" : " nodes)");
			foreach (var node in v.Nodes)
				text.Append("\n  - ").Append(node);
		}

		return text.ToString();
	}

	private static void Walk(JsonElement node, Dictionary<string, JsonElement> byId, List<JsonElement> ordered)
	{
		ordered.Add(node);
		if (!node.TryGetProperty("childIds", out var children))
			return;
		foreach (var child in children.EnumerateArray())
		{
			if (byId.TryGetValue(child.GetString()!, out var next))
				Walk(next, byId, ordered);
		}
	}

	private static int? Level(JsonElement node)
	{
		if (!node.TryGetProperty("properties", out var properties))
			return null;
		foreach (var p in properties.EnumerateArray())
		{
			if (
				p.StringOrNull("name") == "level" && p.TryGetProperty("value", out var v) && v.TryGetProperty("value", out var level) && level.TryGetInt32(
					out var l
				)
			)
				return l;
		}

		return null;
	}

	private static async Task<string> DescribeAsync(Page page, JsonElement node, CancellationToken cancellationToken)
	{
		if (!node.TryGetProperty("backendDOMNodeId", out var backend))
			return "(no element)";
		var resolved = await page.SendAsync("DOM.resolveNode", w => w.WriteNumber("backendNodeId", backend.GetInt64()), cancellationToken).ConfigureAwait(
			false
		);
		var objectId = resolved.GetProperty("object").StringOrNull("objectId");
		if (objectId is null)
			return "(no element)";
		var described = await page.SendAsync(
			"Runtime.callFunctionOn",
			w =>
			{
				w.WriteString("functionDeclaration", DescribeFunction);
				w.WriteString("objectId", objectId);
				w.WriteBoolean("returnByValue", true);
			},
			cancellationToken
		).ConfigureAwait(false);
		await page.SendAsync("Runtime.releaseObject", w => w.WriteString("objectId", objectId), cancellationToken).ConfigureAwait(false);
		return described.GetProperty("result").StringOrNull("value") ?? "(no element)";
	}

	private static async Task<IEnumerable<(Rule, string)>> DomFindingsAsync(Page page, string? selector, CancellationToken cancellationToken)
	{
		var root = await page.SendAsync(
			"Runtime.evaluate",
			w => w.WriteString("expression", selector is null ? "document.documentElement" : $"document.querySelector({JsonSerializerString(selector)})"),
			cancellationToken
		).ConfigureAwait(false);
		var objectId = root.GetProperty("result").StringOrNull("objectId") ?? throw new PageSelectorException(selector ?? "html");
		var result = await page.SendAsync(
			"Runtime.callFunctionOn",
			w =>
			{
				w.WriteString("functionDeclaration", DomRules);
				w.WriteString("objectId", objectId);
				w.WriteBoolean("returnByValue", true);
				w.WriteStartArray("arguments");
				w.WriteStartObject();
				w.WriteBoolean("value", selector is not null);
				w.WriteEndObject();
				w.WriteEndArray();
			},
			cancellationToken
		).ConfigureAwait(false);
		await page.SendAsync("Runtime.releaseObject", w => w.WriteString("objectId", objectId), cancellationToken).ConfigureAwait(false);
		return [
			.. result
				.GetProperty("result")
				.GetProperty("value")
				.EnumerateArray()
				.Select(f => (new Rule(f[0].GetString()!, f[1].GetString()!, f[2].GetString()!), f[3].GetString()!))
		];
	}

	private static string JsonSerializerString(string value) => "\"" + JsonEncodedText.Encode(value) + "\"";

	private static async Task<long> BackendNodeIdAsync(Page page, string selector, CancellationToken cancellationToken)
	{
		var document = await page.SendAsync("DOM.getDocument", w => w.WriteNumber("depth", 0), cancellationToken).ConfigureAwait(false);
		var found = await page.SendAsync(
			"DOM.querySelector",
			w =>
			{
				w.WriteNumber("nodeId", document.GetProperty("root").GetProperty("nodeId").GetInt32());
				w.WriteString("selector", selector);
			},
			cancellationToken
		).ConfigureAwait(false);
		var nodeId = found.GetProperty("nodeId").GetInt32();
		if (nodeId == 0)
			throw new PageSelectorException(selector);
		var described = await page.SendAsync("DOM.describeNode", w => w.WriteNumber("nodeId", nodeId), cancellationToken).ConfigureAwait(false);
		return described.GetProperty("node").GetProperty("backendNodeId").GetInt64();
	}
}
