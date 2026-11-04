using System.Text.Json;

namespace Nullean.Cdp;

/// <summary>Whether a link works.</summary>
/// <param name="Url">The link's absolute URL, without its fragment.</param>
/// <param name="Status">The HTTP status, when there was a response.</param>
/// <param name="Error">Why there was no usable answer: a network error such as <c>net::ERR_NAME_NOT_RESOLVED</c>, or
/// <c>file not found</c>.</param>
public sealed record LinkStatus(string Url, int? Status, string? Error)
{
	/// <summary>A status of 400 or more, or no answer at all.</summary>
	public bool Broken => Status is >= 400 || (Status is null && Error is not null);

	/// <summary>The status, the error, or <c>ok</c> for a file that exists.</summary>
	public string Outcome => Status is { } s ? s.ToString(System.Globalization.CultureInfo.InvariantCulture) : Error ?? "ok";
}

/// <summary>Finds the links on a page and checks whether they work.</summary>
public static class PageLinks
{
	private const string CollectLinks = """
		function() {
			const seen = new Set(), links = [];
			for (const a of this.querySelectorAll('a[href], area[href]')) {
				let url;
				try { url = new URL(a.href); } catch { continue; }
				if (!['http:', 'https:', 'file:'].includes(url.protocol)) continue;
				url.hash = '';
				if (!seen.has(url.href)) { seen.add(url.href); links.push(url.href); }
			}
			return links;
		}
		""";

	/// <summary>
	/// The page's <c>http</c>, <c>https</c> and <c>file</c> links in document order, each once and without its fragment,
	/// or only those inside the first element <paramref name="selector"/> matches.
	/// </summary>
	public static async Task<IReadOnlyList<string>> LinksAsync(this Page page, string? selector = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		var root = await page.SendAsync("Runtime.evaluate", w =>
			w.WriteString("expression", selector is null ? "document" : $"document.querySelector(\"{JsonEncodedText.Encode(selector)}\")"),
			cancellationToken).ConfigureAwait(false);
		var objectId = root.GetProperty("result").StringOrNull("objectId") ?? throw new PageSelectorException(selector ?? "document");
		try
		{
			var result = await page.SendAsync("Runtime.callFunctionOn", w =>
			{
				w.WriteString("functionDeclaration", CollectLinks);
				w.WriteString("objectId", objectId);
				w.WriteBoolean("returnByValue", true);
			}, cancellationToken).ConfigureAwait(false);
			return [.. result.GetProperty("result").GetProperty("value").EnumerateArray().Select(l => l.GetString()!)];
		}
		finally
		{
			await page.SendAsync("Runtime.releaseObject", w => w.WriteString("objectId", objectId), cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Checks <paramref name="urls"/>, a few at a time, in order. Web links are fetched by the browser with a GET, outside
	/// the page, so the page's CORS rules don't apply and nothing the page intercepts sees them; cookies are sent only to
	/// the page's own origin. File links are checked on disk.
	/// </summary>
	public static async Task<IReadOnlyList<LinkStatus>> CheckLinksAsync(this Page page, IReadOnlyList<string> urls, int concurrency = 4, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(page);
		ArgumentNullException.ThrowIfNull(urls);
		var origin = Origin(await page.UrlAsync(cancellationToken).ConfigureAwait(false));
		var results = new LinkStatus[urls.Count];
		await Parallel.ForEachAsync(Enumerable.Range(0, urls.Count), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = cancellationToken },
			async (i, ct) => results[i] = await CheckAsync(page, urls[i], origin, ct).ConfigureAwait(false)).ConfigureAwait(false);
		return results;
	}

	private static string? Origin(string url) =>
		Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : null;

	private static async Task<LinkStatus> CheckAsync(Page page, string url, string? origin, CancellationToken cancellationToken)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			return new LinkStatus(url, null, "not a URL");
		if (uri.IsFile)
			return File.Exists(uri.LocalPath) || Directory.Exists(uri.LocalPath) ? new LinkStatus(url, null, null) : new LinkStatus(url, null, "file not found");
		try
		{
			var loaded = await page.SendAsync("Network.loadNetworkResource", w =>
			{
				w.WriteString("frameId", page.TargetId);
				w.WriteString("url", url);
				w.WriteStartObject("options");
				w.WriteBoolean("disableCache", true);
				w.WriteBoolean("includeCredentials", Origin(url) == origin);
				w.WriteEndObject();
			}, cancellationToken).ConfigureAwait(false);
			var resource = loaded.GetProperty("resource");
			// Only the status is wanted: the body's stream is closed unread.
			if (resource.StringOrNull("stream") is { } stream)
				await page.SendAsync("IO.close", w => w.WriteString("handle", stream), cancellationToken).ConfigureAwait(false);
			int? status = resource.TryGetProperty("httpStatusCode", out var code) && code.TryGetInt32(out var c) ? c : null;
			return new LinkStatus(url, status, status is null ? resource.StringOrNull("netErrorName") ?? "no response" : null);
		}
		catch (Exception e) when (e is not OperationCanceledException and not ObjectDisposedException)
		{
			return new LinkStatus(url, null, e.Message);
		}
	}
}
