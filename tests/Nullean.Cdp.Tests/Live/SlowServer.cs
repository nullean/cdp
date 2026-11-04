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
