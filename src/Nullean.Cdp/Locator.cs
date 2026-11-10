using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Nullean.Cdp;

/// <summary>How a <see cref="Locator"/> finds elements, after agent-browser's <c>find</c> locators.</summary>
public enum LocatorKind
{
	/// <summary>A CSS selector.</summary>
	Css,

	/// <summary>An ARIA role, optionally with an accessible name.</summary>
	Role,

	/// <summary>Visible text; the innermost element holding it.</summary>
	Text,

	/// <summary>The control a <c>&lt;label&gt;</c>, <c>aria-label</c> or <c>aria-labelledby</c> names.</summary>
	Label,

	/// <summary>The <c>placeholder</c> attribute.</summary>
	Placeholder,

	/// <summary>The <c>alt</c> attribute.</summary>
	Alt,

	/// <summary>The <c>title</c> attribute.</summary>
	Title,

	/// <summary>The <c>data-testid</c> attribute, always exact.</summary>
	TestId,
}

/// <summary>What a <see cref="Locator"/> waits for before it acts.</summary>
public enum LocatorState
{
	/// <summary>In the document.</summary>
	Attached,

	/// <summary>In the document with a box and not <c>visibility: hidden</c>.</summary>
	Visible,

	/// <summary>Visible and not disabled: ready for a click or for typing.</summary>
	Actionable,
}

/// <summary>A locator gave up waiting.</summary>
public sealed class LocatorTimeoutException(string message) : TimeoutException(message);

/// <summary>A located element cannot take the action, e.g. filling a button or selecting a missing option.</summary>
public sealed class LocatorException(string message) : Exception(message);

/// <summary>
/// Finds elements on a <see cref="Page"/> and acts on the first match. Every action waits, up to
/// <see cref="Page.ActionTimeout"/>, until a match is in the state it needs; reads wait until one is attached.
/// </summary>
/// <remarks>
/// Text matching follows agent-browser and Playwright: whitespace is collapsed, and matching is a case-insensitive
/// substring unless <c>exact</c>, which compares whole and case-sensitive. Test ids always match exactly.
/// </remarks>
public sealed partial class Locator
{
	private static long GroupCounter;

	// Finds elements for every kind but Role, in document order. Runs with `this` bound to the document.
	private const string FindFunction = """
		function(kind, value, exact) {
			const norm = s => (s || '').replace(/\s+/g, ' ').trim();
			const want = exact ? value : norm(value).toLowerCase();
			const match = s => exact ? norm(s) === value : norm(s).toLowerCase().includes(want);
			const all = () => Array.from(this.querySelectorAll('*'));
			const order = list => [...new Set(list)].sort((a, b) => a === b ? 0 : (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING ? -1 : 1));
			const skip = new Set(['HTML', 'HEAD', 'SCRIPT', 'STYLE', 'TITLE', 'NOSCRIPT', 'TEMPLATE']);
			switch (kind) {
				case 'css': return Array.from(this.querySelectorAll(value));
				case 'text': {
					const hits = all().filter(e => !skip.has(e.tagName) && match(e.innerText ?? e.textContent));
					return hits.filter(e => !hits.some(o => o !== e && e.contains(o)));
				}
				case 'label': {
					const found = [];
					for (const label of this.querySelectorAll('label'))
						if (label.control && match(label.innerText ?? label.textContent)) found.push(label.control);
					for (const e of all()) {
						if (e.hasAttribute('aria-label') && match(e.getAttribute('aria-label'))) found.push(e);
						const ids = e.getAttribute('aria-labelledby');
						if (ids && match(ids.split(/\s+/).map(id => this.getElementById(id)?.innerText ?? '').join(' '))) found.push(e);
					}
					return order(found);
				}
				case 'placeholder': return all().filter(e => e.hasAttribute('placeholder') && match(e.getAttribute('placeholder')));
				case 'alt': return all().filter(e => e.hasAttribute('alt') && match(e.getAttribute('alt')));
				case 'title': return all().filter(e => e.hasAttribute('title') && match(e.getAttribute('title')));
				case 'testid': return all().filter(e => e.getAttribute('data-testid') === value);
			}
			return [];
		}
		""";

	private const string StateFunction = """
		function() {
			const box = this.getBoundingClientRect();
			const style = getComputedStyle(this);
			const visible = box.width > 0 && box.height > 0 && style.visibility !== 'hidden';
			const enabled = !this.disabled && this.getAttribute('aria-disabled') !== 'true' && !this.closest('fieldset[disabled]');
			return [visible, enabled];
		}
		""";

	// Runs with `this` the element to click: what the browser would deliver a click at (x, y) to, and whether that is
	// the element (or something inside it, or the label of a control that is it). Goes into shadow roots, as a click
	// does. pointer-events: none is skipped by elementFromPoint, as it is by a click. Locators find elements of the
	// top document only, so there are no iframe offsets to subtract.
	private const string HitFunction = """
		function(x, y) {
			let hit = this.ownerDocument.elementFromPoint(x, y);
			while (hit && hit.shadowRoot) {
				const inner = hit.shadowRoot.elementFromPoint(x, y);
				if (!inner || inner === hit) break;
				hit = inner;
			}
			if (!hit) return null;
			for (let n = hit; n; n = n.parentNode || n.host) if (n === this) return true;
			const label = hit.closest && hit.closest('label');
			if (label && label.control === this) return true;
			const classes = (hit.getAttribute('class') || '').split(/\s+/).filter(Boolean).slice(0, 2).map(c => '.' + c).join('');
			return hit.tagName.toLowerCase() + (hit.id ? '#' + hit.id : '') + classes;
		}
		""";

	private readonly Page _page;

	// The object handles one locator holds, released after each action. A group per locator, so two locators acting on
	// one page at once never release each other's handles.
	private readonly string _objectGroup = "nullean-cdp-" + Interlocked.Increment(ref GroupCounter).ToString(CultureInfo.InvariantCulture);

	internal Locator(Page page, LocatorKind kind, string value, string? name, bool exact, int? index = null)
	{
		_page = page;
		Kind = kind;
		Value = value;
		Name = name;
		Exact = exact || kind == LocatorKind.TestId;
		Index = index;
	}

	/// <summary>Which match to use: <c>0</c> the first, <c>-1</c> the last, <c>n</c> the n-th from zero; <c>null</c> the first that is ready.</summary>
	public int? Index { get; }

	/// <summary>Only the first match (agent-browser's <c>find first</c>).</summary>
	public Locator First => Nth(0);

	/// <summary>Only the last match (agent-browser's <c>find last</c>).</summary>
	public Locator Last => Nth(-1);

	/// <summary>Only the match at <paramref name="index"/>, from zero; negative counts from the end (agent-browser's <c>find nth</c>).</summary>
	public Locator Nth(int index) => new(_page, Kind, Value, Name, Exact, index);

	/// <summary>How elements are found.</summary>
	public LocatorKind Kind { get; }

	/// <summary>The selector, role, text or attribute value.</summary>
	public string Value { get; }

	/// <summary>The accessible name a <see cref="LocatorKind.Role"/> locator also requires.</summary>
	public string? Name { get; }

	/// <summary>Whether text compares whole and case-sensitive.</summary>
	public bool Exact { get; }

	/// <summary>The locator as agent-browser writes it, for messages: <c>role button --name "Go"</c>.</summary>
	public override string ToString()
	{
		var text = new StringBuilder(Index switch
		{
			null => "",
			0 => "first ",
			-1 => "last ",
			var i => $"nth {i.Value.ToString(CultureInfo.InvariantCulture)} ",
		});
		text.Append(Kind == LocatorKind.Css ? "css " : Kind.ToString().ToLowerInvariant() + " ").Append(Quote(Value));
		if (Name is not null)
			text.Append(" --name ").Append(Quote(Name));
		if (Exact && Kind != LocatorKind.TestId)
			text.Append(" --exact");
		return text.ToString();

		static string Quote(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '#' or '.')
			? s
			: "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}

	/// <summary>How many elements match now, without waiting.</summary>
	public async Task<int> CountAsync(CancellationToken cancellationToken = default)
	{
		try
		{
			return (await FindAsync(cancellationToken).ConfigureAwait(false)).Count;
		}
		finally
		{
			await ReleaseAsync().ConfigureAwait(false);
		}
	}

	/// <summary>Waits until the first match reaches <paramref name="state"/>.</summary>
	public async Task WaitAsync(LocatorState state = LocatorState.Visible, CancellationToken cancellationToken = default)
	{
		try
		{
			await WaitForAsync(state, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			await ReleaseAsync().ConfigureAwait(false);
		}
	}

	/// <summary>Scrolls the element into view and clicks its centre, then waits for any navigation it starts.</summary>
	public Task ClickAsync(CancellationToken cancellationToken = default) =>
		UsingAsync(LocatorState.Actionable, async element =>
		{
			var (x, y) = await ClickPointAsync(element, cancellationToken).ConfigureAwait(false);
			await _page.WithNavigationAsync(() => _page.Mouse.ClickAsync(x, y, cancellationToken), cancellationToken).ConfigureAwait(false);
		}, cancellationToken);

	/// <summary>Double-clicks the element's centre, then waits for any navigation it starts.</summary>
	public Task DoubleClickAsync(CancellationToken cancellationToken = default) =>
		UsingAsync(LocatorState.Actionable, async element =>
		{
			var (x, y) = await ClickPointAsync(element, cancellationToken).ConfigureAwait(false);
			await _page.WithNavigationAsync(() => _page.Mouse.DoubleClickAsync(x, y, cancellationToken), cancellationToken).ConfigureAwait(false);
		}, cancellationToken);

	/// <summary>Gives the element keyboard focus.</summary>
	public Task FocusAsync(CancellationToken cancellationToken = default) =>
		UsingAsync(LocatorState.Visible, element => FocusAsync(element, cancellationToken), cancellationToken);

	/// <summary>Scrolls until the element is in the viewport.</summary>
	public Task ScrollIntoViewAsync(CancellationToken cancellationToken = default) =>
		UsingAsync(LocatorState.Attached, async element =>
			await _page.SendAsync("DOM.scrollIntoViewIfNeeded", w => w.WriteString("objectId", element), cancellationToken).ConfigureAwait(false),
			cancellationToken);

	/// <summary>Scrolls the element itself (a scrollable container) by a number of pixels.</summary>
	public Task ScrollByAsync(double deltaX, double deltaY, CancellationToken cancellationToken = default) =>
		UsingAsync(LocatorState.Visible, async element =>
		{
			var (x, y) = await PointOfAsync(element, cancellationToken).ConfigureAwait(false);
			await _page.Mouse.WheelAsync(x, y, deltaX, deltaY, cancellationToken).ConfigureAwait(false);
		}, cancellationToken);

	/// <summary>
	/// Drags the element onto <paramref name="target"/>: the mouse goes down on its centre, moves to the target's and
	/// comes up. HTML drag and drop is delivered too: Chrome hands the drag over (<c>Input.setInterceptDrags</c>) and it
	/// is dropped on the target.
	/// </summary>
	public Task DragToAsync(Locator target, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(target);
		return UsingAsync(LocatorState.Actionable, async source =>
		{
			var from = await ClickPointAsync(source, cancellationToken).ConfigureAwait(false);
			var to = await target.PointAsync(cancellationToken).ConfigureAwait(false);
			await _page.Mouse.DragAsync(from, to, cancellationToken).ConfigureAwait(false);
		}, cancellationToken);
	}

	// The centre of this locator's element, scrolled into view; for being the other end of a drag. Nothing is pressed
	// there, so what covers it does not matter.
	private async Task<(double X, double Y)> PointAsync(CancellationToken cancellationToken)
	{
		(double, double) point = default;
		await UsingAsync(LocatorState.Visible, async element => point = await PointOfAsync(element, cancellationToken).ConfigureAwait(false),
			cancellationToken).ConfigureAwait(false);
		return point;
	}

	/// <summary>Sets the files of a file input (<c>&lt;input type=file&gt;</c>), as a user picking them would.</summary>
	public Task UploadAsync(IReadOnlyList<string> files, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(files);
		return UsingAsync(LocatorState.Attached, async element =>
		{
			var isFileInput = await CallAsync(element, "function() { return this.tagName === 'INPUT' && this.type === 'file'; }", null, cancellationToken)
				.ConfigureAwait(false);
			if (isFileInput.ValueKind != JsonValueKind.True)
				throw new LocatorException($"Cannot upload to {this}: the element is not a file input.");
			await _page.SendAsync("DOM.setFileInputFiles", w =>
			{
				w.WriteStartArray("files");
				foreach (var file in files)
					w.WriteStringValue(Path.GetFullPath(file));
				w.WriteEndArray();
				w.WriteString("objectId", element);
			}, cancellationToken).ConfigureAwait(false);
		}, cancellationToken);
	}

	/// <summary>The element's box in CSS pixels, as agent-browser's <c>get box</c> prints it.</summary>
	public Task<string> BoxAsync(CancellationToken cancellationToken = default) =>
		ReadStringAsync("""
			function() {
				const r = this.getBoundingClientRect();
				return ['x', 'y', 'width', 'height'].map(k => (k + ':').padEnd(8) + r[k]).join('\n');
			}
			""", cancellationToken);

	/// <summary>A PNG of the element's box, scrolled into view first.</summary>
	public async Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken = default)
	{
		byte[] png = [];
		await UsingAsync(LocatorState.Visible, async element =>
		{
			await _page.SendAsync("DOM.scrollIntoViewIfNeeded", w => w.WriteString("objectId", element), cancellationToken).ConfigureAwait(false);
			var box = await CallAsync(element, "function() { const r = this.getBoundingClientRect(); return [r.x + scrollX, r.y + scrollY, r.width, r.height]; }", null, cancellationToken).ConfigureAwait(false);
			png = await PageScreenshots.CaptureAsync(_page, (box[0].GetDouble(), box[1].GetDouble(), box[2].GetDouble(), box[3].GetDouble()), cancellationToken).ConfigureAwait(false);
		}, cancellationToken).ConfigureAwait(false);
		return png;
	}

	/// <summary>Every computed style of the element, sorted, one <c>property: value</c> per line, as agent-browser's <c>get styles</c>.</summary>
	public Task<string> StylesAsync(CancellationToken cancellationToken = default) =>
		ReadStringAsync("""
			function() {
				const s = getComputedStyle(this);
				return Array.from(s).sort().map(p => p + ': ' + s.getPropertyValue(p)).join('\n');
			}
			""", cancellationToken);

	/// <summary>Moves the mouse over the element's centre.</summary>
	public Task HoverAsync(CancellationToken cancellationToken = default) =>
		UsingAsync(LocatorState.Visible, async element =>
		{
			var (x, y) = await ClickPointAsync(element, cancellationToken).ConfigureAwait(false);
			await _page.Mouse.MoveAsync(x, y, cancellationToken).ConfigureAwait(false);
		}, cancellationToken);

	/// <summary>Replaces the value of an input, text area or editable element, as if typed at once.</summary>
	public Task FillAsync(string text, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		return UsingAsync(LocatorState.Actionable, async element =>
		{
			var editable = await CallAsync(element, """
				function() {
					const editable = this.isContentEditable || this.tagName === 'TEXTAREA'
						|| (this.tagName === 'INPUT' && !['checkbox', 'radio', 'button', 'submit', 'reset', 'file', 'image', 'hidden', 'range', 'color'].includes(this.type));
					if (!editable) return false;
					if (this.readOnly) return 'readonly';
					this.focus();
					if (this.isContentEditable) { const r = document.createRange(); r.selectNodeContents(this); const s = getSelection(); s.removeAllRanges(); s.addRange(r); }
					else this.select();
					return true;
				}
				""", null, cancellationToken).ConfigureAwait(false);
			if (editable.ValueKind == JsonValueKind.False)
				throw new LocatorException($"Cannot fill {this}: the element is not an input, text area or editable element.");
			if (editable.ValueKind == JsonValueKind.String)
				throw new LocatorException($"Cannot fill {this}: the element is read-only.");
			if (text.Length > 0)
				await _page.SendAsync("Input.insertText", w => w.WriteString("text", text), cancellationToken).ConfigureAwait(false);
			else
				await _page.Keyboard.PressAsync("Delete", cancellationToken).ConfigureAwait(false);
		}, cancellationToken);
	}

	/// <summary>Focuses the element and types <paramref name="text"/> one key at a time.</summary>
	public Task TypeAsync(string text, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		return UsingAsync(LocatorState.Actionable, async element =>
		{
			await FocusAsync(element, cancellationToken).ConfigureAwait(false);
			await _page.Keyboard.TypeAsync(text, cancellationToken).ConfigureAwait(false);
		}, cancellationToken);
	}

	/// <summary>Focuses the element and presses <paramref name="key"/> (<c>Enter</c>, <c>Control+a</c>).</summary>
	public Task PressAsync(string key, CancellationToken cancellationToken = default) =>
		UsingAsync(LocatorState.Actionable, async element =>
		{
			await FocusAsync(element, cancellationToken).ConfigureAwait(false);
			await _page.WithNavigationAsync(() => _page.Keyboard.PressAsync(key, cancellationToken), cancellationToken).ConfigureAwait(false);
		}, cancellationToken);

	/// <summary>Checks a checkbox or radio button by clicking it, unless it already is.</summary>
	public Task CheckAsync(CancellationToken cancellationToken = default) => SetCheckedAsync(true, cancellationToken);

	/// <summary>Unchecks a checkbox by clicking it, unless it already is.</summary>
	public Task UncheckAsync(CancellationToken cancellationToken = default) => SetCheckedAsync(false, cancellationToken);

	private Task SetCheckedAsync(bool check, CancellationToken cancellationToken) =>
		UsingAsync(LocatorState.Actionable, async element =>
		{
			const string read = "function() { return this.getAttribute('aria-checked') === 'true' || this.checked === true; }";
			if ((await CallAsync(element, read, null, cancellationToken).ConfigureAwait(false)).ValueKind == (check ? JsonValueKind.True : JsonValueKind.False))
				return;
			var (x, y) = await ClickPointAsync(element, cancellationToken).ConfigureAwait(false);
			await _page.Mouse.ClickAsync(x, y, cancellationToken).ConfigureAwait(false);
			if ((await CallAsync(element, read, null, cancellationToken).ConfigureAwait(false)).ValueKind != (check ? JsonValueKind.True : JsonValueKind.False))
				throw new LocatorException($"Clicking {this} did not {(check ? "check" : "uncheck")} it.");
		}, cancellationToken);

	/// <summary>
	/// Selects the options of a <c>&lt;select&gt;</c> whose value or label is one of <paramref name="values"/>, and
	/// fires <c>input</c> and <c>change</c>. Returns the selected values.
	/// </summary>
	public async Task<IReadOnlyList<string>> SelectAsync(IReadOnlyList<string> values, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(values);
		IReadOnlyList<string> selected = [];
		await UsingAsync(LocatorState.Actionable, async element =>
		{
			var result = await CallAsync(element, """
				function(values) {
					if (this.tagName !== 'SELECT') return { error: 'the element is not a <select>' };
					const options = Array.from(this.options);
					const picked = [];
					for (const v of values) {
						const o = options.find(o => o.value === v) || options.find(o => o.label === v || o.text.trim() === v);
						if (!o) return { error: 'it has no option ' + JSON.stringify(v) };
						picked.push(o);
					}
					if (!this.multiple && picked.length > 1) return { error: 'it does not allow several options' };
					for (const o of options) o.selected = picked.includes(o);
					this.dispatchEvent(new Event('input', { bubbles: true }));
					this.dispatchEvent(new Event('change', { bubbles: true }));
					return { values: picked.map(o => o.value) };
				}
				""", w =>
				{
					w.WriteStartObject();
					w.WriteStartArray("value");
					foreach (var value in values)
						w.WriteStringValue(value);
					w.WriteEndArray();
					w.WriteEndObject();
				}, cancellationToken).ConfigureAwait(false);
			if (result.StringOrNull("error") is { } error)
				throw new LocatorException($"Cannot select in {this}: {error}.");
			selected = [.. result.GetProperty("values").EnumerateArray().Select(v => v.GetString() ?? string.Empty)];
		}, cancellationToken).ConfigureAwait(false);
		return selected;
	}

	/// <summary>The first match's rendered text (<c>innerText</c>).</summary>
	public Task<string> TextAsync(CancellationToken cancellationToken = default) =>
		ReadStringAsync("function() { return this.innerText ?? this.textContent ?? ''; }", cancellationToken);

	/// <summary>The first match's inner HTML.</summary>
	public Task<string> HtmlAsync(CancellationToken cancellationToken = default) =>
		ReadStringAsync("function() { return this.innerHTML; }", cancellationToken);

	/// <summary>The first match's <c>value</c> (an input's text, a select's chosen value).</summary>
	public Task<string> ValueAsync(CancellationToken cancellationToken = default) =>
		ReadStringAsync("function() { return this.value == null ? '' : String(this.value); }", cancellationToken);

	/// <summary>An attribute of the first match, or <c>null</c> when it has none.</summary>
	public async Task<string?> AttributeAsync(string name, CancellationToken cancellationToken = default)
	{
		string? value = null;
		await UsingAsync(LocatorState.Attached, async element =>
		{
			var result = await CallAsync(element, "function(name) { return this.getAttribute(name); }", w =>
			{
				w.WriteStartObject();
				w.WriteString("value", name);
				w.WriteEndObject();
			}, cancellationToken).ConfigureAwait(false);
			value = result.ValueKind == JsonValueKind.String ? result.GetString() : null;
		}, cancellationToken).ConfigureAwait(false);
		return value;
	}

	/// <summary>Whether the first match is visible, now; <c>false</c> when nothing matches.</summary>
	public Task<bool> IsVisibleAsync(CancellationToken cancellationToken = default) => StateNowAsync(s => s.Visible, cancellationToken);

	/// <summary>Whether the first match is enabled, now; <c>false</c> when nothing matches.</summary>
	public Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) => StateNowAsync(s => s.Enabled, cancellationToken);

	/// <summary>Whether the first match is checked; waits until one is attached.</summary>
	public async Task<bool> IsCheckedAsync(CancellationToken cancellationToken = default)
	{
		var isChecked = false;
		await UsingAsync(LocatorState.Attached, async element =>
			isChecked = (await CallAsync(element, "function() { return this.getAttribute('aria-checked') === 'true' || this.checked === true; }", null, cancellationToken)
				.ConfigureAwait(false)).ValueKind == JsonValueKind.True, cancellationToken).ConfigureAwait(false);
		return isChecked;
	}

	private async Task<bool> StateNowAsync(Func<(bool Visible, bool Enabled), bool> pick, CancellationToken cancellationToken)
	{
		try
		{
			var elements = await FindAsync(cancellationToken).ConfigureAwait(false);
			return elements.Count > 0 && pick(await StateAsync(elements[0], cancellationToken).ConfigureAwait(false));
		}
		finally
		{
			await ReleaseAsync().ConfigureAwait(false);
		}
	}

	private async Task<string> ReadStringAsync(string function, CancellationToken cancellationToken)
	{
		var value = string.Empty;
		await UsingAsync(LocatorState.Attached, async element =>
			value = (await CallAsync(element, function, null, cancellationToken).ConfigureAwait(false)).GetString() ?? string.Empty,
			cancellationToken).ConfigureAwait(false);
		return value;
	}

	private async Task UsingAsync(LocatorState state, Func<string, Task> action, CancellationToken cancellationToken)
	{
		try
		{
			await action(await WaitForAsync(state, cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
		}
		finally
		{
			await ReleaseAsync().ConfigureAwait(false);
		}
	}

	/// <summary>Polls until a match is in <paramref name="state"/> and returns its object id.</summary>
	private async Task<string> WaitForAsync(LocatorState state, CancellationToken cancellationToken)
	{
		var timeout = _page.ActionTimeout;
		var started = System.Diagnostics.Stopwatch.StartNew();
		while (true)
		{
			var elements = await FindAsync(cancellationToken).ConfigureAwait(false);
			var states = new List<(bool Visible, bool Enabled)>();
			foreach (var element in elements)
			{
				if (state == LocatorState.Attached)
					return element;
				var s = await StateAsync(element, cancellationToken).ConfigureAwait(false);
				if (s.Visible && (state == LocatorState.Visible || s.Enabled))
					return element;
				states.Add(s);
			}

			if (started.Elapsed >= timeout)
				throw new LocatorTimeoutException(
					$"Timed out after {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s waiting for {this} to be {Describe(state)}: {Diagnose(states)}.");
			await ReleaseAsync().ConfigureAwait(false);
			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}
	}

	private static string Describe(LocatorState state) => state switch
	{
		LocatorState.Attached => "in the page",
		LocatorState.Visible => "visible",
		_ => "visible and enabled",
	};

	private static string Diagnose(List<(bool Visible, bool Enabled)> states)
	{
		if (states.Count == 0)
			return "no element matches";
		var visible = states.Count(s => s.Visible);
		var count = states.Count == 1 ? "1 element matches" : $"{states.Count} elements match";
		return visible == 0 ? $"{count}, none visible" : $"{count}, {(visible == 1 ? "the visible one is" : "the visible ones are")} disabled";
	}

	/// <summary>Object ids of the matching elements in document order, narrowed to <see cref="Index"/> when set.</summary>
	private async Task<IReadOnlyList<string>> FindAsync(CancellationToken cancellationToken)
	{
		var all = await FindAllAsync(cancellationToken).ConfigureAwait(false);
		if (Index is not { } index)
			return all;
		var at = index < 0 ? all.Count + index : index;
		return at >= 0 && at < all.Count ? [all[at]] : [];
	}

	private async Task<IReadOnlyList<string>> FindAllAsync(CancellationToken cancellationToken)
	{
		if (Kind == LocatorKind.Role)
			return await FindByRoleAsync(cancellationToken).ConfigureAwait(false);

		var document = await _page.SendAsync("Runtime.evaluate", w =>
		{
			w.WriteString("expression", "document");
			w.WriteString("objectGroup", _objectGroup);
		}, cancellationToken).ConfigureAwait(false);
		var documentId = document.GetProperty("result").GetProperty("objectId").GetString() ?? throw new LocatorException($"Cannot look up {this}: the page returned no object.");
		var found = await _page.SendAsync("Runtime.callFunctionOn", w =>
		{
			w.WriteString("functionDeclaration", FindFunction);
			w.WriteString("objectId", documentId);
			w.WriteString("objectGroup", _objectGroup);
			w.WriteStartArray("arguments");
			w.WriteStartObject();
			w.WriteString("value", Kind == LocatorKind.TestId ? "testid" : Kind.ToString().ToLowerInvariant());
			w.WriteEndObject();
			w.WriteStartObject();
			w.WriteString("value", Value);
			w.WriteEndObject();
			w.WriteStartObject();
			w.WriteBoolean("value", Exact);
			w.WriteEndObject();
			w.WriteEndArray();
		}, cancellationToken).ConfigureAwait(false);
		if (found.TryGetProperty("exceptionDetails", out var details))
		{
			var message = details.TryGetProperty("exception", out var e) && e.StringOrNull("description") is { } d ? d.Split('\n')[0] : "script error";
			throw new LocatorException($"Cannot look up {this}: {message}");
		}

		var arrayId = found.GetProperty("result").GetProperty("objectId").GetString() ?? throw new LocatorException($"Cannot look up {this}: the page returned no object.");
		var properties = await _page.SendAsync("Runtime.getProperties", w =>
		{
			w.WriteString("objectId", arrayId);
			w.WriteBoolean("ownProperties", true);
		}, cancellationToken).ConfigureAwait(false);

		var elements = new List<(int Index, string ObjectId)>();
		foreach (var property in properties.GetProperty("result").EnumerateArray())
		{
			if (int.TryParse(property.GetProperty("name").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
				&& property.TryGetProperty("value", out var value) && value.StringOrNull("objectId") is { } objectId)
				elements.Add((index, objectId));
		}

		return [.. elements.OrderBy(e => e.Index).Select(e => e.ObjectId)];
	}

	// Role lookups read the whole tree rather than use Accessibility.queryAXTree, which stopped answering when several
	// pages were open at once (Chrome 156; see DECISIONS.md).
	private async Task<IReadOnlyList<string>> FindByRoleAsync(CancellationToken cancellationToken)
	{
		var tree = await _page.SendAsync("Accessibility.getFullAXTree", null, cancellationToken).ConfigureAwait(false);
		var ids = new List<string>();
		foreach (var node in tree.GetProperty("nodes").EnumerateArray())
		{
			if (node.TryGetProperty("ignored", out var ignored) && ignored.ValueKind == JsonValueKind.True)
				continue;
			// As in agent-browser, the role itself always ignores case; only the name follows Exact.
			if (!node.TryGetProperty("role", out var role) || !string.Equals(role.StringOrNull("value"), Value, StringComparison.OrdinalIgnoreCase))
				continue;
			if (!node.TryGetProperty("backendDOMNodeId", out var backend))
				continue;
			if (Name is not null && !Matches(node.TryGetProperty("name", out var n) ? n.StringOrNull("value") ?? string.Empty : string.Empty))
				continue;

			var resolved = await _page.SendAsync("DOM.resolveNode", w =>
			{
				w.WriteNumber("backendNodeId", backend.GetInt64());
				w.WriteString("objectGroup", _objectGroup);
			}, cancellationToken).ConfigureAwait(false);
			if (resolved.GetProperty("object").StringOrNull("objectId") is { } objectId)
				ids.Add(objectId);
		}

		return ids;
	}

	private bool Matches(string actual)
	{
		var normalised = string.Join(' ', actual.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
		return Exact
			? normalised == Name
			: normalised.Contains(string.Join(' ', Name!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);
	}

	private async Task<(bool Visible, bool Enabled)> StateAsync(string objectId, CancellationToken cancellationToken)
	{
		var state = await CallAsync(objectId, StateFunction, null, cancellationToken).ConfigureAwait(false);
		return (state[0].GetBoolean(), state[1].GetBoolean());
	}

	private async Task<JsonElement> CallAsync(string objectId, string function, Action<Utf8JsonWriter>? argument, CancellationToken cancellationToken)
	{
		var result = await _page.SendAsync("Runtime.callFunctionOn", w =>
		{
			w.WriteString("functionDeclaration", function);
			w.WriteString("objectId", objectId);
			w.WriteBoolean("returnByValue", true);
			w.WriteBoolean("awaitPromise", true);
			if (argument is not null)
			{
				w.WriteStartArray("arguments");
				argument(w);
				w.WriteEndArray();
			}
		}, cancellationToken).ConfigureAwait(false);
		if (result.TryGetProperty("exceptionDetails", out var details))
		{
			var message = details.TryGetProperty("exception", out var e) && e.StringOrNull("description") is { } d ? d.Split('\n')[0] : "script error";
			throw new LocatorException($"Acting on {this} failed: {message}");
		}

		return result.TryGetProperty("result", out var r) && r.TryGetProperty("value", out var value) ? value : default;
	}

	private async Task FocusAsync(string objectId, CancellationToken cancellationToken) =>
		await _page.SendAsync("DOM.focus", w => w.WriteString("objectId", objectId), cancellationToken).ConfigureAwait(false);

	// The point a click would press, once nothing else would receive it: a cover (a modal, a banner), or a point off the
	// screen or clipped, is waited out like any other state, and reported with what is in the way when it does not go.
	private async Task<(double X, double Y)> ClickPointAsync(string objectId, CancellationToken cancellationToken)
	{
		var timeout = _page.ActionTimeout;
		var started = System.Diagnostics.Stopwatch.StartNew();
		while (true)
		{
			var (x, y) = await PointOfAsync(objectId, cancellationToken).ConfigureAwait(false);
			var hit = await CallAsync(objectId, HitFunction, w =>
			{
				w.WriteStartObject();
				w.WriteNumber("value", x);
				w.WriteEndObject();
				w.WriteStartObject();
				w.WriteNumber("value", y);
				w.WriteEndObject();
			}, cancellationToken).ConfigureAwait(false);
			if (hit.ValueKind == JsonValueKind.True)
				return (x, y);
			if (started.Elapsed >= timeout)
			{
				var point = $"({x.ToString("0.#", CultureInfo.InvariantCulture)}, {y.ToString("0.#", CultureInfo.InvariantCulture)})";
				var cause = hit.ValueKind == JsonValueKind.String
					? $"<{hit.GetString()}> would receive it instead"
					: "nothing is there: it is outside the viewport or clipped";
				throw new LocatorTimeoutException(
					$"Timed out after {timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)}s waiting for {this} to receive a click at {point}: {cause}.");
			}

			await Task.Delay(50, cancellationToken).ConfigureAwait(false);
		}
	}

	// The centre of the element, scrolled into view.
	private async Task<(double X, double Y)> PointOfAsync(string objectId, CancellationToken cancellationToken)
	{
		await _page.SendAsync("DOM.scrollIntoViewIfNeeded", w => w.WriteString("objectId", objectId), cancellationToken).ConfigureAwait(false);
		var quads = await _page.SendAsync("DOM.getContentQuads", w => w.WriteString("objectId", objectId), cancellationToken).ConfigureAwait(false);
		foreach (var quad in quads.GetProperty("quads").EnumerateArray())
		{
			var p = quad.EnumerateArray().Select(v => v.GetDouble()).ToArray();
			if (p.Length != 8)
				continue;
			var (x, y) = ((p[0] + p[2] + p[4] + p[6]) / 4, (p[1] + p[3] + p[5] + p[7]) / 4);
			if (Area(p) > 0.99)
				return (x, y);
		}

		throw new LocatorException($"Cannot click {this}: it has no visible box.");
	}

	// The shoelace formula: a quad with no area has no point to click.
	private static double Area(double[] p)
	{
		var sum = 0.0;
		for (var i = 0; i < 8; i += 2)
		{
			var (x1, y1, x2, y2) = (p[i], p[i + 1], p[(i + 2) % 8], p[(i + 3) % 8]);
			sum += (x1 * y2) - (x2 * y1);
		}

		return Math.Abs(sum) / 2;
	}

	private async Task ReleaseAsync()
	{
		if (_page.Connection.IsClosed)
			return;
		try
		{
			await _page.SendAsync("Runtime.releaseObjectGroup", w => w.WriteString("objectGroup", _objectGroup)).ConfigureAwait(false);
		}
		catch (Exception e) when (e is Protocol.CdpException or Protocol.CdpDisconnectedException)
		{
			// The page navigated or closed; its objects are gone anyway.
		}
	}
}
