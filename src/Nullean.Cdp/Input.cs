using System.Collections.Frozen;

namespace Nullean.Cdp;

/// <summary>The mouse, in CSS pixels of the viewport.</summary>
public sealed class Mouse
{
	private readonly Page _page;

	internal Mouse(Page page) => _page = page;

	/// <summary>Moves the pointer, firing <c>mousemove</c>, <c>mouseenter</c> and <c>mouseover</c>.</summary>
	public Task MoveAsync(double x, double y, CancellationToken cancellationToken = default) => DispatchAsync("mouseMoved", x, y, "none", 0, cancellationToken);

	/// <summary>Moves to the point, presses and releases the left button.</summary>
	public async Task ClickAsync(double x, double y, CancellationToken cancellationToken = default)
	{
		await MoveAsync(x, y, cancellationToken).ConfigureAwait(false);
		await DispatchAsync("mousePressed", x, y, "left", 1, cancellationToken).ConfigureAwait(false);
		await DispatchAsync("mouseReleased", x, y, "left", 1, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Two clicks, the second reporting a click count of two, so <c>dblclick</c> fires.</summary>
	public async Task DoubleClickAsync(double x, double y, CancellationToken cancellationToken = default)
	{
		await MoveAsync(x, y, cancellationToken).ConfigureAwait(false);
		for (var count = 1; count <= 2; count++)
		{
			await DispatchAsync("mousePressed", x, y, "left", count, cancellationToken).ConfigureAwait(false);
			await DispatchAsync("mouseReleased", x, y, "left", count, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>Turns the wheel at a point: positive <paramref name="deltaY"/> scrolls down, positive <paramref name="deltaX"/> right.</summary>
	public async Task WheelAsync(double x, double y, double deltaX, double deltaY, CancellationToken cancellationToken = default) =>
		await _page.SendAsync(
			"Input.dispatchMouseEvent",
			w =>
			{
				w.WriteString("type", "mouseWheel");
				w.WriteNumber("x", x);
				w.WriteNumber("y", y);
				w.WriteNumber("deltaX", deltaX);
				w.WriteNumber("deltaY", deltaY);
			},
			cancellationToken
		).ConfigureAwait(false);

	/// <summary>
	/// Presses at <paramref name="from"/>, moves to <paramref name="to"/> in steps and releases. When the page starts an
	/// HTML drag, Chrome hands it over and it is dropped at <paramref name="to"/>.
	/// </summary>
	public async Task DragAsync((double X, double Y) from, (double X, double Y) to, CancellationToken cancellationToken = default)
	{
		var intercepted = new TaskCompletionSource<System.Text.Json.JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		using var listening = _page.Connection.On(_page.SessionId, e =>
		{
			if (e.Method == "Input.dragIntercepted" && e.Params.TryGetProperty("data", out var data))
				intercepted.TrySetResult(data.Clone());
		});
		await _page.SendAsync("Input.setInterceptDrags", w => w.WriteBoolean("enabled", true), cancellationToken).ConfigureAwait(false);
		try
		{
			await MoveAsync(from.X, from.Y, cancellationToken).ConfigureAwait(false);
			await DispatchAsync("mousePressed", from.X, from.Y, "left", 1, cancellationToken).ConfigureAwait(false);
			// Moves with the left button held, so Chrome starts a drag.
			const int steps = 5;
			for (var i = 1; i <= steps; i++)
			{
				var (x, y) = (from.X + ((to.X - from.X) * i / steps), from.Y + ((to.Y - from.Y) * i / steps));
				await DispatchAsync("mouseMoved", x, y, "left", 0, cancellationToken, buttons: 1).ConfigureAwait(false);
			}

			// An HTML drag is handed over shortly after the moves; a plain mouse drag never is.
			if (await Task.WhenAny(intercepted.Task, Task.Delay(250, cancellationToken)).ConfigureAwait(false) == intercepted.Task)
			{
				var data = await intercepted.Task.ConfigureAwait(false);
				foreach (var type in new[] { "dragEnter", "dragOver", "drop" })
				{
					await _page.SendAsync(
						"Input.dispatchDragEvent",
						w =>
						{
							w.WriteString("type", type);
							w.WriteNumber("x", to.X);
							w.WriteNumber("y", to.Y);
							w.WritePropertyName("data");
							data.WriteTo(w);
						},
						cancellationToken
					).ConfigureAwait(false);
				}
			}

			await DispatchAsync("mouseReleased", to.X, to.Y, "left", 1, cancellationToken).ConfigureAwait(false);
		}
		finally
		{
			await _page.SendAsync("Input.setInterceptDrags", w => w.WriteBoolean("enabled", false), CancellationToken.None).ConfigureAwait(false);
		}
	}

	private async Task DispatchAsync(string type, double x, double y, string button, int clickCount, CancellationToken cancellationToken, int buttons = -1) =>
		await _page.SendAsync(
			"Input.dispatchMouseEvent",
			w =>
			{
				w.WriteString("type", type);
				w.WriteNumber("x", x);
				w.WriteNumber("y", y);
				w.WriteString("button", button);
				w.WriteNumber("clickCount", clickCount);
				if (buttons >= 0)
					w.WriteNumber("buttons", buttons);
			},
			cancellationToken
		).ConfigureAwait(false);
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
		["Space"] = " ",
		["Esc"] = "Escape",
		["Return"] = "Enter",
		["Ctrl"] = "Control",
		["Cmd"] = "Meta",
		["Command"] = "Meta",
		["Option"] = "Alt",
		["Up"] = "ArrowUp",
		["Down"] = "ArrowDown",
		["Left"] = "ArrowLeft",
		["Right"] = "ArrowRight",
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

	/// <summary>Inserts <paramref name="text"/> at the focus at once, without key events, as a paste would.</summary>
	public async Task InsertTextAsync(string text, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(text);
		await _page.SendAsync("Input.insertText", w => w.WriteString("text", text), cancellationToken).ConfigureAwait(false);
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
		var names = keys == "+" ? ["+"] : keys.EndsWith("++", StringComparison.Ordinal) ? [.. keys[..^2].Split('+'), "+"] : keys.Split('+');
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
				throw new ArgumentException(
					$"'{names[i]}' in '{keys}' is not a modifier: only Shift, Control, Alt and Meta can come before '+'.",
					nameof(keys)
				);
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
		await _page.SendAsync(
			"Input.dispatchKeyEvent",
			w =>
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
			},
			cancellationToken
		).ConfigureAwait(false);
}
