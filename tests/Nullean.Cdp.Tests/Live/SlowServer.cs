using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullean.Cdp.Tests.Live;

/// <summary>
/// A local HTTP server whose page holds an image that answers late, so the page's load event comes well after the
/// navigation commits. file:// pages load too fast to tell waiting for load from not waiting.
/// </summary>
internal sealed class SlowServer : IAsyncDisposable
{
	private static readonly byte[] Pixel = Convert.FromBase64String(
		"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII=");

	private readonly HttpListener _listener = new();
	private readonly Task _serving;

	public SlowServer(TimeSpan imageDelay)
	{
		var port = FreePort();
		// localhost, not 127.0.0.1: HttpListener on Windows needs no URL reservation for it.
		Url = $"http://localhost:{port}/";
		_listener.Prefixes.Add(Url);
		_listener.Start();
		_serving = Task.Run(() => ServeAsync(imageDelay));
	}

	public string Url { get; }

	private async Task ServeAsync(TimeSpan imageDelay)
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
			{
				return;
			}

			_ = Task.Run(async () =>
			{
				var response = context.Response;
				try
				{
					if (context.Request.Url!.AbsolutePath == "/headers")
					{
						response.ContentType = "text/html; charset=utf-8";
						await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
							$"<!doctype html><title>Headers</title><p id=\"x\">{WebUtility.HtmlEncode(context.Request.Headers["x-test"] ?? "none")}</p>"));
					}
					else if (context.Request.Url.AbsolutePath == "/telemetry")
					{
						// What the browser SDK does: JSON posted to a collector, some of it slow, one refused, one large.
						response.ContentType = "text/html; charset=utf-8";
						await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
							"<!doctype html><title>Telemetry</title><link rel=\"icon\" href=\"data:,\"><h1>Telemetry</h1><script>"
							+ "const post = (path, body) => fetch(path, { method: 'POST', body });"
							+ "post('/collect/l', JSON.stringify({ resourceLogs: [{ resource: { attributes: [{ key: 'service.name', value: { stringValue: 'docs-frontend' } }] },"
							+ " scopeLogs: [{ logRecords: [{ eventName: 'browser.navigation_timing' }] }] }] }));"
							+ "post('/collect/big', 'x'.repeat(100000)); post('/collect/deny', '{}'); post('/collect/slow', '{}');</script>"));
					}
					else if (context.Request.Url.AbsolutePath == "/spa")
					{
						// One button that changes the URL without a new document, and one link that is a full navigation.
						response.ContentType = "text/html; charset=utf-8";
						await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
							"<!doctype html><title>Spa</title><link rel=\"icon\" href=\"data:,\"><h1>Spa</h1>"
							+ "<button id=\"push\" onclick=\"history.pushState(null, '', '/spa/inner')\">Push</button> <a id=\"full\" href=\"/next\">Full</a>"));
					}
					else if (context.Request.Url.AbsolutePath.StartsWith("/collect/", StringComparison.Ordinal))
					{
						await context.Request.InputStream.CopyToAsync(Stream.Null);
						if (context.Request.Url.AbsolutePath == "/collect/slow")
							await Task.Delay(1500);
						response.StatusCode = context.Request.Url.AbsolutePath == "/collect/deny" ? 403 : 200;
					}
					else if (context.Request.Url.AbsolutePath == "/private")
					{
						// Basic auth for user "admin" with password "hunter22".
						if (context.Request.Headers["Authorization"] != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:hunter22")))
						{
							response.StatusCode = 401;
							response.AddHeader("WWW-Authenticate", "Basic realm=\"test\"");
						}
						else
						{
							response.ContentType = "text/html; charset=utf-8";
							await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes("<!doctype html><title>Private</title><p>Welcome</p>"));
						}
					}
					else if (context.Request.Url.AbsolutePath.StartsWith("/missing", StringComparison.Ordinal))
						response.StatusCode = 404;
					else if (context.Request.Url.AbsolutePath == "/links")
					{
						// Same-site links that work and one that doesn't, a broken image, and links to other sites: the one in
						// ?other=, and a host that never resolves.
						var other = context.Request.QueryString["other"] is { } o ? $"<a href=\"{WebUtility.HtmlEncode(o)}\">Other site</a> " : "";
						response.ContentType = "text/html; charset=utf-8";
						await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
							"<!doctype html><title>Links</title><link rel=\"icon\" href=\"data:,\"><main><h1>Links</h1><img src=\"/missing.png\" alt=\"gone\">"
							+ "<nav><a href=\"/next\">Next</a> <a href=\"/next#top\">Next again</a> <a href=\"/missing-page\">Gone</a> <a href=\"mailto:x@example.com\">Mail</a></nav>"
							+ $"<footer>{other}<a href=\"https://no-such-host.invalid/\">Nowhere</a></footer></main>"));
					}
					else if (context.Request.Url.AbsolutePath == "/slow.png")
					{
						await Task.Delay(imageDelay);
						response.ContentType = "image/png";
						await response.OutputStream.WriteAsync(Pixel);
					}
					else
					{
						response.ContentType = "text/html; charset=utf-8";
						var title = context.Request.Url.AbsolutePath == "/next" ? "Next" : "Slow";
						await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(
							$"<!doctype html><title>{title}</title><link rel=\"icon\" href=\"data:,\"><h1>{title}</h1><img src=\"/slow.png\" alt=\"late\"><a href=\"/next\">Next page</a>"));
					}

					response.Close();
				}
				catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
				{
				}
			});
		}
	}

	private static int FreePort()
	{
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		return ((IPEndPoint)probe.LocalEndpoint).Port;
	}

	public async ValueTask DisposeAsync()
	{
		_listener.Stop();
		_listener.Close();
		await _serving;
	}
}
