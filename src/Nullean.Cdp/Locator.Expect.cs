using System.Text.Json;

namespace Nullean.Cdp;

public sealed partial class Locator
{
	/// <summary>
	/// Checks <paramref name="matcher"/> against the first match, again and again until it holds or the timeout passes,
	/// and returns what happened without throwing. This is what test framework adapters report.
	/// </summary>
	/// <param name="matcher">What to check.</param>
	/// <param name="options">The timeout (the page's <see cref="Page.ActionTimeout"/> by default), negation and text options.</param>
	/// <param name="cancellationToken">Stops the check.</param>
	public Task<ExpectationResult> CheckAsync(Matcher matcher, ExpectOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(matcher);
		if (matcher.IsPageLevel)
			throw new ArgumentException($"{matcher.Kind} is about the page, not an element: use Page.CheckAsync.", nameof(matcher));
		options ??= new ExpectOptions();
		return Expectations.RunAsync(matcher, options, _page.ActionTimeout, ToString(), ct => ObserveAsync(matcher, options, ct), cancellationToken);
	}

	/// <summary>Like <see cref="CheckAsync(Matcher, ExpectOptions?, CancellationToken)"/>, but throws an <see cref="ExpectationException"/> when the assertion does not hold.</summary>
	public async Task ExpectAsync(Matcher matcher, ExpectOptions? options = null, CancellationToken cancellationToken = default)
	{
		var result = await CheckAsync(matcher, options, cancellationToken).ConfigureAwait(false);
		if (!result.Passed)
			throw new ExpectationException(result);
	}

	private const string CheckedFunction = "function() { return this.getAttribute('aria-checked') === 'true' || this.checked === true; }";
	private const string FocusedFunction = "function() { return document.activeElement === this; }";

	private const string EditableFunction = """
		function() {
			const editable = ['INPUT', 'TEXTAREA', 'SELECT'].includes(this.tagName) || this.isContentEditable;
			if (!editable) return false;
			const readOnly = this.readOnly === true || this.getAttribute('aria-readonly') === 'true';
			const disabled = this.disabled === true || this.getAttribute('aria-disabled') === 'true' || !!this.closest('fieldset[disabled]');
			return !readOnly && !disabled;
		}
		""";

	private const string EmptyFunction = """
		function() {
			if (this.tagName === 'INPUT' || this.tagName === 'TEXTAREA') return this.value === '';
			return (this.textContent ?? '').trim() === '';
		}
		""";

	private const string TextFunction = "function(inner) { return (inner ? (this.innerText ?? this.textContent) : this.textContent) ?? ''; }";
	private const string ValueFunction = "function() { return this.value == null ? '' : String(this.value); }";
	private const string AttributeFunction = "function(name) { return this.getAttribute(name); }";
	private const string ClassFunction = "function() { return this.getAttribute('class') ?? ''; }";

	// One look at the page for a matcher, without waiting: the first match's state, or that there is none.
	private async Task<Observation> ObserveAsync(Matcher matcher, ExpectOptions options, CancellationToken cancellationToken)
	{
		try
		{
			var elements = await FindAsync(cancellationToken).ConfigureAwait(false);
			if (matcher.Kind == MatcherKind.Count)
				return new Observation(elements.Count > 0, Count: elements.Count);
			if (elements.Count == 0)
				return default;
			var element = elements[0];
			switch (matcher.Kind)
			{
				case MatcherKind.Visible or MatcherKind.Hidden:
					return new Observation(true, (await StateAsync(element, cancellationToken).ConfigureAwait(false)).Visible);
				case MatcherKind.Enabled or MatcherKind.Disabled:
					return new Observation(true, (await StateAsync(element, cancellationToken).ConfigureAwait(false)).Enabled);
				case MatcherKind.Checked or MatcherKind.Unchecked:
					return new Observation(true, await FlagAsync(element, CheckedFunction, cancellationToken).ConfigureAwait(false));
				case MatcherKind.Focused:
					return new Observation(true, await FlagAsync(element, FocusedFunction, cancellationToken).ConfigureAwait(false));
				case MatcherKind.Editable:
					return new Observation(true, await FlagAsync(element, EditableFunction, cancellationToken).ConfigureAwait(false));
				case MatcherKind.Empty:
					return new Observation(true, await FlagAsync(element, EmptyFunction, cancellationToken).ConfigureAwait(false));
				case MatcherKind.Attached:
					return new Observation(true);
				case MatcherKind.Text:
					return new Observation(true, Text: (await CallAsync(element, TextFunction, w =>
					{
						w.WriteStartObject();
						w.WriteBoolean("value", options.UseInnerText);
						w.WriteEndObject();
					}, cancellationToken).ConfigureAwait(false)).GetString() ?? string.Empty);
				case MatcherKind.Value:
					return new Observation(true, Text: (await CallAsync(element, ValueFunction, null, cancellationToken).ConfigureAwait(false)).GetString() ?? string.Empty);
				case MatcherKind.Attribute:
					var attribute = await CallAsync(element, AttributeFunction, w =>
					{
						w.WriteStartObject();
						w.WriteString("value", matcher.Name);
						w.WriteEndObject();
					}, cancellationToken).ConfigureAwait(false);
					return new Observation(true, Text: attribute.ValueKind == JsonValueKind.String ? attribute.GetString() : null);
				case MatcherKind.Class:
					return new Observation(true, Text: (await CallAsync(element, ClassFunction, null, cancellationToken).ConfigureAwait(false)).GetString() ?? string.Empty);
				default:
					throw new ArgumentException($"{matcher.Kind} is not an element matcher.", nameof(matcher));
			}
		}
		finally
		{
			await ReleaseAsync().ConfigureAwait(false);
		}
	}

	private async Task<bool> FlagAsync(string element, string function, CancellationToken cancellationToken) =>
		(await CallAsync(element, function, null, cancellationToken).ConfigureAwait(false)).ValueKind == JsonValueKind.True;
}
