using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp;

/// <summary>One request the page made, with what came back.</summary>
public sealed class NetworkRequest
{
	internal NetworkRequest(string id, string url, string method, string resourceType, double startedSeconds, double wallTime, JsonElement headers)
	{
		Id = id;
		Url = url;
		Method = method;
		ResourceType = resourceType;
		StartedSeconds = startedSeconds;
		WallTime = wallTime;
		RequestHeaders = headers;
	}

	internal string Id { get; }

	/// <summary>The URL.</summary>
	public string Url { get; }

	/// <summary>GET, POST, …</summary>
	public string Method { get; }

	/// <summary>Document, Script, Image, XHR, Fetch, …</summary>
	public string ResourceType { get; }

	/// <summary>The status code, or <c>null</c> without a response.</summary>
	public int? Status { get; internal set; }

	/// <summary>The status text.</summary>
	public string StatusText { get; internal set; } = string.Empty;

	/// <summary>The response's MIME type.</summary>
	public string MimeType { get; internal set; } = string.Empty;

	/// <summary>The protocol, e.g. <c>h2</c> or <c>http/1.1</c>.</summary>
	public string Protocol { get; internal set; } = string.Empty;

	/// <summary>Bytes received over the network, headers included.</summary>
	public long EncodedBytes { get; internal set; }

	/// <summary>The error when the request failed (<c>net::ERR_…</c>).</summary>
	public string? Error { get; internal set; }

	/// <summary>Whether it was answered from the cache.</summary>
	public bool FromCache { get; internal set; }

	internal double StartedSeconds { get; }

	internal double WallTime { get; }

	internal double? FinishedSeconds { get; set; }

	internal JsonElement RequestHeaders { get; }

	/// <summary>Whether the request is over: it was answered completely or it failed.</summary>
	public bool IsFinished => FinishedSeconds is not null;

	internal byte[]? PostBytes { get; set; }

	/// <summary>The body's size in bytes when it came with the request; <c>null</c> without a body, or while it is still being asked for.</summary>
	public long? PostDataLength => PostBytes?.LongLength;

	internal Task<byte[]?>? PostFetch { get; set; }

	/// <summary>
	/// The request body, or <c>null</c> when there is none or Chrome no longer has it. It is read when the request starts, so a
	/// page that has since navigated away still has it.
	/// </summary>
	public async Task<byte[]?> GetPostDataAsync(CancellationToken cancellationToken = default) =>
		PostBytes ?? (PostFetch is { } fetch ? await fetch.WaitAsync(cancellationToken).ConfigureAwait(false) : null);

	internal JsonElement ResponseHeaders { get; set; }

	internal JsonElement Timing { get; set; }

	/// <summary>As agent-browser's <c>network requests</c> prints it, without its run-specific id: <c>GET url (Type) 200</c>.</summary>
	public override string ToString() =>
		$"{Method} {Url} ({ResourceType}){(Status is { } s ? " " + s.ToString(CultureInfo.InvariantCulture) : Error is { } e ? " " + e : "")}";
}

/// <summary>
/// The requests of one page from the moment recording started, from the protocol's <c>Network</c> events: enough
/// for a request list and a HAR file with per-phase timings. Response bodies are not kept.
/// </summary>
public sealed class NetworkRecording : IAsyncDisposable
{
	private static readonly string[] SensitiveHeaders = ["authorization", "proxy-authorization", "cookie", "set-cookie"];

	private readonly Page _page;
	private readonly IDisposable _listening;
	private readonly ConcurrentDictionary<string, NetworkRequest> _open = new(StringComparer.Ordinal);
	private ConcurrentQueue<NetworkRequest> _requests = new();

	private NetworkRecording(Page page)
	{
		_page = page;
		_listening = page.Connection.On(page.SessionId, OnEvent);
	}

	internal static async Task<NetworkRecording> StartAsync(Page page, CancellationToken cancellationToken)
	{
		var recording = new NetworkRecording(page);
		await page.SendAsync("Network.enable", null, cancellationToken).ConfigureAwait(false);
		return recording;
	}

	/// <summary>The requests recorded so far, in the order they started.</summary>
	public IReadOnlyList<NetworkRequest> Requests => [.. Volatile.Read(ref _requests)];

	/// <summary>Forgets the requests recorded so far.</summary>
	public void Clear() => Interlocked.Exchange(ref _requests, new ConcurrentQueue<NetworkRequest>());

	private void OnEvent(CdpEvent e)
	{
		var p = e.Params;
		switch (e.Method)
		{
			case "Network.requestWillBeSent" when p.StringOrNull("requestId") is { } id && p.TryGetProperty("request", out var request):
				// A redirect reuses the id: the previous hop ends with the redirect response.
				if (p.TryGetProperty("redirectResponse", out var redirect) && _open.TryGetValue(id, out var hop))
				{
					Respond(hop, redirect);
					hop.FinishedSeconds = p.GetProperty("timestamp").GetDouble();
				}

				var started = new NetworkRequest(id, request.StringOrNull("url") ?? string.Empty, request.StringOrNull("method") ?? "GET",
					p.StringOrNull("type") ?? "Other", p.GetProperty("timestamp").GetDouble(),
					p.TryGetProperty("wallTime", out var wall) ? wall.GetDouble() : 0,
					request.TryGetProperty("headers", out var headers) ? headers.Clone() : default);
				CapturePostData(started, request);
				_open[id] = started;
				Volatile.Read(ref _requests).Enqueue(started);
				break;
			case "Network.responseReceived" when p.StringOrNull("requestId") is { } id && _open.TryGetValue(id, out var target):
				Respond(target, p.GetProperty("response"));
				break;
			case "Network.loadingFinished" when p.StringOrNull("requestId") is { } id && _open.TryRemove(id, out var finished):
				finished.FinishedSeconds = p.GetProperty("timestamp").GetDouble();
				if (p.TryGetProperty("encodedDataLength", out var length))
					finished.EncodedBytes = (long)length.GetDouble();
				break;
			case "Network.loadingFailed" when p.StringOrNull("requestId") is { } id && _open.TryRemove(id, out var failed):
				failed.FinishedSeconds = p.GetProperty("timestamp").GetDouble();
				failed.Error = p.StringOrNull("errorText");
				break;
		}
	}

	// The body comes with the event when Chrome inlines it (as entries of bytes, or as text); otherwise it is asked for now,
	// before a navigation can drop it.
	private void CapturePostData(NetworkRequest started, JsonElement request)
	{
		if (!request.TryGetProperty("hasPostData", out var has) || has.ValueKind != JsonValueKind.True)
			return;
		if (request.TryGetProperty("postDataEntries", out var entries) && entries.ValueKind == JsonValueKind.Array
			&& entries.EnumerateArray().All(e => e.TryGetProperty("bytes", out _)))
		{
			started.PostBytes = [.. entries.EnumerateArray().SelectMany(e => Convert.FromBase64String(e.GetProperty("bytes").GetString() ?? string.Empty))];
			return;
		}

		if (request.StringOrNull("postData") is { } text && !request.TryGetProperty("postDataEntries", out _))
		{
			started.PostBytes = Encoding.UTF8.GetBytes(text);
			return;
		}

		started.PostFetch = FetchPostDataAsync(started.Id);
	}

	private async Task<byte[]?> FetchPostDataAsync(string requestId)
	{
		try
		{
			var reply = await _page.SendAsync("Network.getRequestPostData", w => w.WriteString("requestId", requestId)).ConfigureAwait(false);
			var data = reply.StringOrNull("postData") ?? string.Empty;
			return reply.TryGetProperty("base64Encoded", out var encoded) && encoded.ValueKind == JsonValueKind.True
				? Convert.FromBase64String(data)
				: Encoding.UTF8.GetBytes(data);
		}
		catch (Exception e) when (e is not OutOfMemoryException)
		{
			// Chrome no longer has it, or the page is gone.
			return null;
		}
	}

	private static void Respond(NetworkRequest request, JsonElement response)
	{
		request.Status = response.TryGetProperty("status", out var status) ? status.GetInt32() : null;
		request.StatusText = response.StringOrNull("statusText") ?? string.Empty;
		request.MimeType = response.StringOrNull("mimeType") ?? string.Empty;
		request.Protocol = response.StringOrNull("protocol") ?? string.Empty;
		request.FromCache = (response.TryGetProperty("fromDiskCache", out var disk) && disk.ValueKind == JsonValueKind.True)
			|| (response.TryGetProperty("fromServiceWorker", out var worker) && worker.ValueKind == JsonValueKind.True);
		request.ResponseHeaders = response.TryGetProperty("headers", out var headers) ? headers.Clone() : default;
		request.Timing = response.TryGetProperty("timing", out var timing) ? timing.Clone() : default;
	}

	/// <summary>
	/// The recording as a HAR 1.2 file, which DevTools and HAR viewers draw as a waterfall. Authorization and cookie
	/// header values are replaced by <c>[redacted]</c>; <paramref name="redact"/> can scrub more (secret values).
	/// </summary>
	public string ToHar(Func<string, string>? redact = null) => ToHar(Requests, redact);

	/// <summary>The given requests as a HAR 1.2 file; see <see cref="ToHar(Func{string, string}?)"/>.</summary>
	public static string ToHar(IEnumerable<NetworkRequest> requests, Func<string, string>? redact = null)
	{
		ArgumentNullException.ThrowIfNull(requests);
		var buffer = new ArrayBufferWriter<byte>();
		using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
		{
			json.WriteStartObject();
			json.WriteStartObject("log");
			json.WriteString("version", "1.2");
			json.WriteStartObject("creator");
			json.WriteString("name", "Nullean.Cdp");
			json.WriteString("version", typeof(NetworkRecording).Assembly.GetName().Version?.ToString(3) ?? "0");
			json.WriteEndObject();
			json.WriteStartArray("pages");
			json.WriteEndArray();
			json.WriteStartArray("entries");
			foreach (var request in requests)
				WriteEntry(json, request);
			json.WriteEndArray();
			json.WriteEndObject();
			json.WriteEndObject();
		}

		var har = Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
		return redact is null ? har : redact(har);
	}

	private static void WriteEntry(Utf8JsonWriter json, NetworkRequest request)
	{
		var (blocked, dns, connect, ssl, send, wait, receive, total) = Timings(request);
		json.WriteStartObject();
		json.WriteString("startedDateTime", DateTimeOffset.FromUnixTimeMilliseconds((long)(request.WallTime * 1000)).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
		json.WriteNumber("time", Math.Round(total, 3));
		json.WriteString("_resourceType", request.ResourceType);
		if (request.Error is { } error)
			json.WriteString("_error", error);

		json.WriteStartObject("request");
		json.WriteString("method", request.Method);
		json.WriteString("url", request.Url);
		json.WriteString("httpVersion", request.Protocol.Length > 0 ? request.Protocol : "HTTP/1.1");
		json.WriteStartArray("cookies");
		json.WriteEndArray();
		WriteHeaders(json, request.RequestHeaders);
		WriteQuery(json, request.Url);
		json.WriteNumber("headersSize", -1);
		json.WriteNumber("bodySize", -1);
		json.WriteEndObject();

		json.WriteStartObject("response");
		json.WriteNumber("status", request.Status ?? 0);
		json.WriteString("statusText", request.StatusText);
		json.WriteString("httpVersion", request.Protocol.Length > 0 ? request.Protocol : "HTTP/1.1");
		json.WriteStartArray("cookies");
		json.WriteEndArray();
		WriteHeaders(json, request.ResponseHeaders);
		json.WriteStartObject("content");
		json.WriteNumber("size", -1);
		json.WriteString("mimeType", request.MimeType);
		json.WriteEndObject();
		json.WriteString("redirectURL", HeaderValue(request.ResponseHeaders, "location") ?? string.Empty);
		json.WriteNumber("headersSize", -1);
		json.WriteNumber("bodySize", request.EncodedBytes > 0 ? request.EncodedBytes : -1);
		json.WriteNumber("_transferSize", request.EncodedBytes);
		json.WriteEndObject();

		json.WriteStartObject("cache");
		json.WriteEndObject();
		json.WriteStartObject("timings");
		json.WriteNumber("blocked", Math.Round(blocked, 3));
		json.WriteNumber("dns", Math.Round(dns, 3));
		json.WriteNumber("connect", Math.Round(connect, 3));
		json.WriteNumber("ssl", Math.Round(ssl, 3));
		json.WriteNumber("send", Math.Round(send, 3));
		json.WriteNumber("wait", Math.Round(wait, 3));
		json.WriteNumber("receive", Math.Round(receive, 3));
		json.WriteEndObject();
		json.WriteEndObject();
	}

	/// <summary>
	/// HAR phases in ms from Chrome's resource timing, as DevTools exports them: <c>-1</c> for a phase that did not
	/// happen (a reused connection has no DNS or connect); <c>ssl</c> is part of <c>connect</c>.
	/// </summary>
	internal static (double Blocked, double Dns, double Connect, double Ssl, double Send, double Wait, double Receive, double Total) Timings(NetworkRequest request)
	{
		var total = request.FinishedSeconds is { } end ? Math.Max(0, (end - request.StartedSeconds) * 1000) : 0;
		if (request.Timing.ValueKind != JsonValueKind.Object)
			return (0, -1, -1, -1, 0, 0, total, total);
		var t = request.Timing;
		double At(string name) => t.TryGetProperty(name, out var v) ? v.GetDouble() : -1;
		double Span(double start, double finish) => start >= 0 && finish >= 0 ? finish - start : -1;
		var requestTime = At("requestTime");
		var (dnsStart, dnsEnd, connectStart, connectEnd) = (At("dnsStart"), At("dnsEnd"), At("connectStart"), At("connectEnd"));
		var (sslStart, sslEnd, sendStart, sendEnd, headersEnd) = (At("sslStart"), At("sslEnd"), At("sendStart"), At("sendEnd"), At("receiveHeadersEnd"));
		// Queueing before the request time, and stalls before the first phase that happened.
		var queued = requestTime > 0 ? Math.Max(0, (requestTime - request.StartedSeconds) * 1000) : 0;
		var firstPhase = new[] { dnsStart, connectStart, sendStart }.Where(v => v >= 0).DefaultIfEmpty(0).Min();
		var blocked = queued + firstPhase;
		var send = Math.Max(0, Span(sendStart, sendEnd));
		var wait = Math.Max(0, Span(sendEnd, headersEnd));
		var receive = request.FinishedSeconds is { } finished && requestTime > 0 && headersEnd >= 0
			? Math.Max(0, ((finished - requestTime) * 1000) - headersEnd)
			: 0;
		var dns = Span(dnsStart, dnsEnd);
		var connect = Span(connectStart, connectEnd);
		var sum = blocked + Math.Max(0, dns) + Math.Max(0, connect) + send + wait + receive;
		return (blocked, dns, connect, Span(sslStart, sslEnd), send, wait, receive, sum);
	}

	private static void WriteHeaders(Utf8JsonWriter json, JsonElement headers)
	{
		json.WriteStartArray("headers");
		if (headers.ValueKind == JsonValueKind.Object)
		{
			foreach (var header in headers.EnumerateObject())
			{
				json.WriteStartObject();
				json.WriteString("name", header.Name);
				var sensitive = SensitiveHeaders.Contains(header.Name, StringComparer.OrdinalIgnoreCase);
				json.WriteString("value", sensitive ? "[redacted]" : header.Value.ToString());
				json.WriteEndObject();
			}
		}

		json.WriteEndArray();
	}

	private static void WriteQuery(Utf8JsonWriter json, string url)
	{
		json.WriteStartArray("queryString");
		if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Query.Length > 1)
		{
			foreach (var pair in uri.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
			{
				var parts = pair.Split('=', 2);
				json.WriteStartObject();
				json.WriteString("name", Uri.UnescapeDataString(parts[0]));
				json.WriteString("value", parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : string.Empty);
				json.WriteEndObject();
			}
		}

		json.WriteEndArray();
	}

	private static string? HeaderValue(JsonElement headers, string name) =>
		headers.ValueKind != JsonValueKind.Object ? null
		: headers.EnumerateObject().Where(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value.ToString()).FirstOrDefault();

	/// <summary>Stops recording.</summary>
	public ValueTask DisposeAsync()
	{
		_listening.Dispose();
		_ = _page;
		return ValueTask.CompletedTask;
	}
}
