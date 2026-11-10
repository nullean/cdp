using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullean.Cdp.Tests.Live;

/// <summary>
/// A local HTTP server with a page that can write three ways (a fetch, a form post, a beacon) and counts what reached
/// it. file:// pages cannot show a refused POST: nothing answers it either way.
/// </summary>
internal sealed class WriteServer : IAsyncDisposable
{
	private const string Page = """
		<!doctype html><title>Writes</title><link rel="icon" href="data:,">
		<button onclick="fetch('/write', {method: 'POST', body: 'x'})">Save with fetch</button>
		<button onclick="fetch('/telemetry', {method: 'POST', body: 'x'})">Send telemetry</button>
		<form method="post" action="/form"><button>Save with form</button></form>
		<a href="/next">Next page</a>
		""";

	private readonly HttpListener _listener = new();
	private readonly Task _serving;
	private int _writes;

	public WriteServer()
	{
		Url = $"http://localhost:{FreePort()}/";
		_listener.Prefixes.Add(Url);
		_listener.Start();
		_serving = Task.Run(ServeAsync);
	}

	public string Url { get; }

	/// <summary>The requests that reached the server with a method other than GET.</summary>
	public int Writes => Volatile.Read(ref _writes);

	private async Task ServeAsync()
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

			var response = context.Response;
			try
			{
				if (context.Request.HttpMethod != "GET")
					Interlocked.Increment(ref _writes);
				response.ContentType = "text/html; charset=utf-8";
				var title = context.Request.Url!.AbsolutePath == "/next" ? "<title>Next</title><h1>Next</h1>" : context.Request.HttpMethod == "GET" ? Page : "<title>Saved</title><h1>Saved</h1>";
				await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(title));
				response.Close();
			}
			catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
			{
			}
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
