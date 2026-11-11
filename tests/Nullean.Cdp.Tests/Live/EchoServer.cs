using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullean.Cdp.Tests.Live;

/// <summary>
/// A local HTTP server whose /echo page shows what reached it (two request headers, the user agent) and what the page
/// itself sees (navigator.userAgent, the viewport width), so a run's defaults can be checked from both sides.
/// </summary>
internal sealed class EchoServer : IAsyncDisposable
{
	private readonly HttpListener _listener = new();
	private readonly Task _serving;

	public EchoServer()
	{
		Url = $"http://localhost:{FreePort()}/";
		_listener.Prefixes.Add(Url);
		_listener.Start();
		_serving = Task.Run(ServeAsync);
	}

	public string Url { get; }

	private async Task ServeAsync()
	{
		while (_listener.IsListening)
		{
			HttpListenerContext context;
			try
			{
				context = await _listener.GetContextAsync();
			}
			catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException or ArgumentException)
			{
				return;
			}

			var response = context.Response;
			try
			{
				string Header(string name) => WebUtility.HtmlEncode(context.Request.Headers[name] ?? "none");

				response.ContentType = "text/html; charset=utf-8";
				await response.OutputStream.WriteAsync(
					Encoding.UTF8.GetBytes(
						"<!doctype html><title>Echo</title><link rel=\"icon\" href=\"data:,\">" +
							$"<p id=\"header\">{Header("x-docs-synthetic-monitor")}</p><p id=\"journey\">{Header("x-journey")}</p><p id=\"request-ua\">{Header("user-agent")}</p>" +
							"<p id=\"ua\"></p><p id=\"width\"></p>" +
							"<script>document.getElementById('ua').textContent = navigator.userAgent; document.getElementById('width').textContent = String(innerWidth);</script>"
					)
				);
				response.Close();
			}
			catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
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
