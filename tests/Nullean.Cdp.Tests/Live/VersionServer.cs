using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nullean.Cdp.Tests.Live;

/// <summary>
/// A local HTTP server whose page names a build in a header and in a meta tag, and whose build changes as requests come in: a
/// deploy that lands a moment after the run starts. The server can start late, or never: until it does, the port refuses connections, as an origin that is still coming up does.
/// </summary>
internal sealed class VersionServer : IAsyncDisposable
{
	private readonly HttpListener _listener = new();
	private readonly Task _serving;
	private readonly Func<int, string> _build;
	private int _requests;

	public VersionServer(Func<int, string> build, TimeSpan? startAfter = null)
	{
		_build = build;
		var port = FreePort();
		Url = $"http://localhost:{port}/";
		_listener.Prefixes.Add(Url);
		_serving = Task.Run(async () =>
		{
			if (startAfter == Timeout.InfiniteTimeSpan)
				return;
			if (startAfter is { } wait)
				await Task.Delay(wait);
			_listener.Start();
			await ServeAsync();
		});
	}

	public string Url { get; }

	public int Requests => Volatile.Read(ref _requests);

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

			if (context.Request.Url!.AbsolutePath == "/favicon.ico")
			{
				context.Response.StatusCode = 404;
				context.Response.Close();
				continue;
			}

			var number = Interlocked.Increment(ref _requests);
			_ = Task.Run(() => RespondAsync(context, number));
		}
	}

	private async Task RespondAsync(HttpListenerContext context, int number)
	{
		try
		{
			var build = _build(number);
			context.Response.ContentType = "text/html; charset=utf-8";
			context.Response.AddHeader("x-build", build);
			await context
				.Response
				.OutputStream
				.WriteAsync(
					Encoding.UTF8.GetBytes(
						$"<!doctype html><title>Landing</title><link rel=\"icon\" href=\"data:,\"><meta name=\"docs:build\" content=\"{build}\"><h1 id=\"build\">{build}</h1>"
					)
				);
			context.Response.Close();
		}
		catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException) { }
	}

	private static int FreePort()
	{
		using var probe = new TcpListener(IPAddress.Loopback, 0);
		probe.Start();
		return ((IPEndPoint)probe.LocalEndpoint).Port;
	}

	public async ValueTask DisposeAsync()
	{
		if (_listener.IsListening)
			_listener.Stop();
		_listener.Close();
		await _serving;
	}
}
