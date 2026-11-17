using System.Text.Json;

namespace Nullean.Cdp;

/// <summary>The response that served a page's document: its URL, status and headers.</summary>
/// <param name="Url">The URL the response came from.</param>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Headers">The response headers by name, case-insensitive. A header sent more than once is joined with <c>, </c> (Chrome joins them with a newline).</param>
public sealed record PageResponse(string Url, int Status, IReadOnlyDictionary<string, string> Headers)
{
	/// <summary>The value of <paramref name="name"/>, or <c>null</c> when the response has no such header.</summary>
	public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

	internal static PageResponse From(JsonElement response)
	{
		var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		if (response.TryGetProperty("headers", out var sent) && sent.ValueKind == JsonValueKind.Object)
			foreach (var header in sent.EnumerateObject())
				headers[header.Name] = (header.Value.GetString() ?? string.Empty).Replace("\n", ", ", StringComparison.Ordinal);
		return new PageResponse(
			response.StringOrNull("url") ?? string.Empty,
			response.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Number ? status.GetInt32() : 0,
			headers
		);
	}
}
