using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp;

/// <summary>
/// What the page is waiting on and what already went wrong, kept so a timeout can say why it timed out: the requests still in
/// flight (oldest first), and the last failed or error-status ones. Holds URLs and counts only, never bodies or headers.
/// </summary>
internal sealed class PageActivity
{
	private const int Shown = 5;
	private const int Kept = 20;

	private readonly ConcurrentDictionary<string, InFlight> _open = new(StringComparer.Ordinal);
	private readonly ConcurrentQueue<string> _problems = new();

	private readonly record struct InFlight(string Method, string Url, long Started);

	public void OnEvent(CdpEvent e)
	{
		var p = e.Params;
		if (p.StringOrNull("requestId") is not { } id)
			return;
		switch (e.Method)
		{
			// A redirect reuses the id: the new hop replaces the old one.
			case "Network.requestWillBeSent" when p.TryGetProperty("request", out var request) && request.StringOrNull("url") is { } url:
				_open[id] = new InFlight(request.StringOrNull("method") ?? "GET", url, Stopwatch.GetTimestamp());
				break;
			case "Network.responseReceived" when p.TryGetProperty("response", out var response) && response.TryGetProperty(
				"status",
				out var status
			) && status.GetInt32() >= 400:
				Problem($"{status.GetInt32()} {(_open.TryGetValue(id, out var failed) ? failed.Url : response.StringOrNull("url"))}");
				break;
			case "Network.loadingFinished":
				_ = _open.TryRemove(id, out _);
				break;
			case "Network.loadingFailed":
				var known = _open.TryRemove(id, out var open);
				// A request the page cancelled itself, such as an image it replaced, is not a failure.
				if (p.TryGetProperty("canceled", out var canceled) && canceled.ValueKind == System.Text.Json.JsonValueKind.True)
					break;
				Problem($"{p.StringOrNull("errorText") ?? "failed"} {(known ? open.Url : id)}");
				break;
		}
	}

	private void Problem(string text)
	{
		_problems.Enqueue(text);
		while (_problems.Count > Kept && _problems.TryDequeue(out _))
		{ }
	}

	/// <summary>The lines that say what is in flight and what failed; none when neither.</summary>
	public IReadOnlyList<string> Describe()
	{
		var lines = new List<string>();
		var now = Stopwatch.GetTimestamp();
		var open = _open.Values.OrderBy(r => r.Started).ToList();
		if (open.Count > 0)
		{
			var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"{open.Count} request{(open.Count == 1 ? "" : "s")} still in flight"));
			foreach (var request in open.Take(Shown))
				_ = text.Append(
					CultureInfo.InvariantCulture,
					$"\n    {request.Method} {request.Url} ({Stopwatch.GetElapsedTime(request.Started, now).TotalSeconds:0.0}s)"
				);
			if (open.Count > Shown)
				_ = text.Append(CultureInfo.InvariantCulture, $"\n    and {open.Count - Shown} more");
			lines.Add(text.ToString());
		}

		var problems = _problems.ToArray();
		if (problems.Length > 0)
			lines.Add("last failed requests: " + string.Join("; ", problems.TakeLast(Shown)));
		return lines;
	}
}
