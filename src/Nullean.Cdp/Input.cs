using System.Collections.Frozen;

namespace Nullean.Cdp;

/// <summary>The mouse, in CSS pixels of the viewport.</summary>
public sealed class Mouse
{
	private readonly Page _page;

	internal Mouse(Page page) => _page = page;

	/// <summary>Moves the pointer, firing <c>mousemove</c>, <c>mouseenter</c> and <c>mouseover</c>.</summary>
	public Task MoveAsync(double x, double y, CancellationToken cancellationToken = default) =>
		DispatchAsync("mouseMoved", x, y, "none", 0, cancellationToken);

	/// <summary>Moves to the point, presses and releases the left button.</summary>
	public async Task ClickAsync(double x, double y, CancellationToken cancellationToken = default)
	{
		await MoveAsync(x, y, cancellationToken).ConfigureAwait(false);
		await DispatchAsync("mousePressed", x, y, "left", 1, cancellationToken).ConfigureAwait(false);
		await DispatchAsync("mouseReleased", x, y, "left", 1, cancellationToken).ConfigureAwait(false);
	}

	private async Task DispatchAsync(string type, double x, double y, string button, int clickCount, CancellationToken cancellationToken) =>
		await _page.SendAsync("Input.dispatchMouseEvent", w =>
		{
			w.WriteString("type", type);
			w.WriteNumber("x", x);
			w.WriteNumber("y", y);
			w.WriteString("button", button);
			w.WriteNumber("clickCount", clickCount);
		}, cancellationToken).ConfigureAwait(false);
}

/// <summary>The keyboard, typing into whatever has focus.</summary>
public sealed class Keyboard
{
	private readonly record struct Key(string Name, string Code, int KeyCode, string? Text);

	// Named keys as agent-browser and Playwright spell them.
	private static readonly FrozenDictionary<string, Key> Named = new Key[]
	{
		new("Enter", "Enter", 13, "\r"),
		new("Tab", "Tab", 9, null),
		new("Escape", "Escape", 27, null),
		new("Backspace", "Backspace", 8, null),
		new("Delete", "Delete", 46, null),
		new("ArrowLeft", "ArrowLeft", 37, null),
		new("ArrowUp", "ArrowUp", 38, null),
		new("ArrowRight", "ArrowRight", 39, null),
		new("ArrowDown", "ArrowDown", 40, null),
		new("Home", "Home", 36, null),
		new("End", "End", 35, null),
		new("PageUp", "PageUp", 33, null),
		new("PageDown", "PageDown", 34, null),
		new(" ", "Space", 32, " "),
		new("Shift", "ShiftLeft", 16, null),
		new("Control", "ControlLeft", 17, null),
		new("Alt", "AltLeft", 18, null),
		new("Meta", "MetaLeft", 91, null),
	}.ToFrozenDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);

	private static readonly FrozenDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["Space"] = " ", ["Esc"] = "Escape", ["Return"] = "Enter", ["Ctrl"] = "Control", ["Cmd"] = "Meta", ["Command"] = "Meta",
		["Option"] = "Alt", ["Up"] = "ArrowUp", ["Down"] = "ArrowDown", ["Left"] = "ArrowLeft", ["Right"] = "ArrowRight",
	}.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

	private readonly Page _page;

	internal Keyboard(Page page) => _page = page;

	/// <summary>
	/// Presses a key or a chord: <c>Enter</c>, <c>a</c>, <c>Control+a</c>, <c>Shift+Tab</c>. Modifiers go down in order,
	/// the key is pressed, then they come up in reverse.
	/// </summary>
	public async Task PressAsync(string keys, CancellationToken cancellationToken = default)
	{
		var parts = Parse(keys);
		var modifiers = 0;
		foreach (var modifier in parts[..^1])
		{
			modifiers |= Flag(modifier.Name);
			await DispatchAsync("keyDown", modifier, modifiers, withText: false, cancellationToken).ConfigureAwait(false);
		}

		var key = parts[^1];
		// With Control, Alt or Meta held a key is a shortcut and types nothing; Shift only changes the character.
		var shifted = (modifiers & 8) != 0 && key.Text is { Length: 1 } t && char.IsAsciiLetterLower(t[0])
			? key with { Name = t.ToUpperInvariant(), Text = t.ToUpperInvariant() }
			: key;
		await DispatchAsync("keyDown", shifted, modifiers, withText: (modifiers & 7) == 0, cancellationToken).ConfigureAwait(false);
		await DispatchAsync("keyUp", shifted, modifiers, withText: false, cancellationToken).ConfigureAwait(false);

		for (var i = parts.Length - 2; i >= 0; i--)
		{
			modifiers &= ~Flag(parts[i].Name);
			await DispatchAsync("keyUp", parts[i], modifiers, withText: false, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Types each character of <paramref name="text"/> as a key press.</summary>
	public async Task TypeAsync(string text, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		foreach (var rune in text.EnumerateRunes())
		{
			var key = Character(rune.ToString());
			await DispatchAsync("keyDown", key, 0, withText: true, cancellationToken).ConfigureAwait(false);
			await DispatchAsync("keyUp", key, 0, withText: false, cancellationToken).ConfigureAwait(false);
		}
	}

	private static Key[] Parse(string keys)
	{
		ArgumentException.ThrowIfNullOrEmpty(keys);
		// "+" alone, or a trailing "+" as in "Shift++", is the plus key itself.
		var names = keys == "+" ? ["+"] : keys.EndsWith("++", StringComparison.Ordinal)
			? [.. keys[..^2].Split('+'), "+"]
			: keys.Split('+');
		var parsed = new Key[names.Length];
		for (var i = 0; i < names.Length; i++)
		{
			var name = Aliases.TryGetValue(names[i], out var alias) ? alias : names[i];
			if (Named.TryGetValue(name, out var key))
				parsed[i] = key;
			else if (name.Length == 1 || (name.Length == 2 && char.IsSurrogatePair(name, 0)))
				parsed[i] = Character(name);
			else
				throw new ArgumentException($"Unknown key '{names[i]}' in '{keys}'.", nameof(keys));
			if (i < names.Length - 1 && Flag(parsed[i].Name) == 0)
				throw new ArgumentException($"'{names[i]}' in '{keys}' is not a modifier: only Shift, Control, Alt and Meta can come before '+'.", nameof(keys));
		}

		return parsed;
	}

	private static Key Character(string c)
	{
		if (Named.TryGetValue(c, out var named))
			return named;
		var ch = c[0];
		if (c.Length == 1 && char.IsAsciiLetter(ch))
			return new Key(c, "Key" + char.ToUpperInvariant(ch), char.ToUpperInvariant(ch), c);
		if (c.Length == 1 && char.IsAsciiDigit(ch))
			return new Key(c, "Digit" + ch, ch, c);
		return new Key(c, string.Empty, 0, c);
	}

	private static int Flag(string name) => name switch
	{
		"Alt" => 1,
		"Control" => 2,
		"Meta" => 4,
		"Shift" => 8,
		_ => 0,
	};

	private async Task DispatchAsync(string type, Key key, int modifiers, bool withText, CancellationToken cancellationToken) =>
		await _page.SendAsync("Input.dispatchKeyEvent", w =>
		{
			w.WriteString("type", type);
			w.WriteString("key", key.Name);
			if (key.Code.Length > 0)
				w.WriteString("code", key.Code);
			if (key.KeyCode != 0)
			{
				w.WriteNumber("windowsVirtualKeyCode", key.KeyCode);
				w.WriteNumber("nativeVirtualKeyCode", key.KeyCode);
			}

			w.WriteNumber("modifiers", modifiers);
			if (withText && key.Text is { } text)
			{
				w.WriteString("text", text);
				w.WriteString("unmodifiedText", text);
			}
		}, cancellationToken).ConfigureAwait(false);
}
