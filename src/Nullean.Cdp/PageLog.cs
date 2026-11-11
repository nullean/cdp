using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp;

/// <summary>A console call on the page: its type (<c>log</c>, <c>warning</c>, <c>error</c>, …) and its arguments as text.</summary>
public sealed record ConsoleMessage(string Type, string Text)
{
	/// <summary>As agent-browser's <c>console</c> prints it: <c>[type] text</c>.</summary>
	public override string ToString() => $"[{Type}] {Text}";
}

/// <summary>An uncaught exception or unhandled promise rejection on the page.</summary>
/// <param name="Message">The first line of the error, e.g. <c>Error: boom</c>.</param>
/// <param name="Location">Where it was thrown, <c>url:line:column</c> (1-based), or empty when unknown.</param>
public sealed record PageError(string Message, string Location)
{
	/// <summary>The message, then where it was thrown.</summary>
	public override string ToString() => Location.Length == 0 ? Message : $"{Message} ({Location})";
}

/// <summary>Collects a page's console calls and uncaught errors from the moment it is created.</summary>
internal sealed class PageLog
{
	private ConcurrentQueue<ConsoleMessage> _console = new();
	private ConcurrentQueue<PageError> _errors = new();

	public IReadOnlyList<ConsoleMessage> Console => [.. Volatile.Read(ref _console)];

	public IReadOnlyList<PageError> Errors => [.. Volatile.Read(ref _errors)];

	public void ClearConsole() => Interlocked.Exchange(ref _console, new ConcurrentQueue<ConsoleMessage>());

	public void ClearErrors() => Interlocked.Exchange(ref _errors, new ConcurrentQueue<PageError>());

	public void OnEvent(CdpEvent e)
	{
		switch (e.Method)
		{
			case "Runtime.consoleAPICalled":
				var args = e.Params.TryGetProperty("args", out var a) ? a.EnumerateArray().Select(Describe) : [];
				Volatile.Read(ref _console).Enqueue(new ConsoleMessage(e.Params.StringOrNull("type") ?? "log", string.Join(' ', args)));
				break;
			case "Runtime.exceptionThrown" when e.Params.TryGetProperty("exceptionDetails", out var details):
				Volatile.Read(ref _errors).Enqueue(Error(details));
				break;
		}
	}

	private static PageError Error(JsonElement details)
	{
		var description = details.TryGetProperty("exception", out var exception)
			? exception.StringOrNull("description") ?? (exception.TryGetProperty("value", out var v) ? v.ToString() : null)
			: null;
		var message = (description ?? details.StringOrNull("text") ?? "Uncaught error").Split('\n')[0].Trim();

		string? url = null;
		int line = 0, column = 0;
		if (details.TryGetProperty("stackTrace", out var stack) && stack.TryGetProperty("callFrames", out var frames) && frames.GetArrayLength() > 0)
		{
			var top = frames[0];
			url = top.StringOrNull("url");
			line = top.GetProperty("lineNumber").GetInt32();
			column = top.GetProperty("columnNumber").GetInt32();
		}
		else if (details.StringOrNull("url") is { Length: > 0 } scriptUrl)
		{
			url = scriptUrl;
			line = details.GetProperty("lineNumber").GetInt32();
			column = details.GetProperty("columnNumber").GetInt32();
		}

		var location = string.IsNullOrEmpty(url) ? string.Empty : string.Create(CultureInfo.InvariantCulture, $"{url}:{line + 1}:{column + 1}");
		return new PageError(message, location);
	}

	// Arguments as agent-browser prints them: strings bare, objects and arrays from their preview, strings in them quoted.
	internal static string Describe(JsonElement remote)
	{
		var type = remote.StringOrNull("type");
		if (remote.StringOrNull("unserializableValue") is { } unserializable)
			return unserializable;
		return type switch
		{
			"string" => remote.StringOrNull("value") ?? string.Empty,
			"number" or "boolean" when remote.TryGetProperty("value", out var value) => value.GetRawText(),
			"undefined" => "undefined",
			"object" when remote.StringOrNull("subtype") == "null" => "null",
			"object" when remote.TryGetProperty("preview", out var preview) => Preview(preview),
			_ => remote.StringOrNull("description") ?? type ?? string.Empty,
		};
	}

	private static string Preview(JsonElement preview)
	{
		var isArray = preview.StringOrNull("subtype") == "array";
		var text = new StringBuilder(isArray ? "[" : "{");
		var first = true;
		foreach (var property in preview.TryGetProperty("properties", out var properties) ? properties.EnumerateArray() : default)
		{
			if (!first)
				text.Append(", ");
			first = false;
			if (!isArray)
				text.Append(property.StringOrNull("name")).Append(": ");
			var value = property.StringOrNull("value") ?? property.StringOrNull("type") ?? string.Empty;
			text.Append(property.StringOrNull("type") == "string" ? $"\"{value}\"" : value);
		}

		if (preview.TryGetProperty("overflow", out var overflow) && overflow.ValueKind == JsonValueKind.True)
			text.Append(first ? "…" : ", …");
		return text.Append(isArray ? ']' : '}').ToString();
	}
}
