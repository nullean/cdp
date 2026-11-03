using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp;

/// <summary>How <see cref="Browser.LaunchAsync"/> starts Chrome.</summary>
public sealed class BrowserLaunchOptions
{
	/// <summary>The executable; found with <see cref="ChromeLocator"/> when <c>null</c>.</summary>
	public string? ExecutablePath { get; init; }

	/// <summary>Run without a window. Defaults to <c>true</c>.</summary>
	public bool Headless { get; init; } = true;

	/// <summary>How long Chrome may take to open its DevTools port. Defaults to 30 seconds.</summary>
	public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(30);

	/// <summary>Extra command line arguments, after the ones this library sets.</summary>
	public IReadOnlyList<string> Arguments { get; init; } = [];
}

/// <summary>Chrome could not be found or started.</summary>
public sealed class BrowserLaunchException(string message) : Exception(message);

/// <summary>
/// A Chrome process (or a browser this library connected to) and the one CDP connection to it. Contexts made with
/// <see cref="NewContextAsync"/> keep cookies and storage apart, so parallel tests can share one browser.
/// </summary>
public sealed class Browser : IAsyncDisposable
{
	private readonly Process? _process;
	private readonly string? _userDataDirectory;
	private int _disposed;

	private Browser(CdpConnection connection, Process? process, string? userDataDirectory)
	{
		Connection = connection;
		_process = process;
		_userDataDirectory = userDataDirectory;
	}

	/// <summary>The connection, for commands this library does not wrap.</summary>
	public CdpConnection Connection { get; }

	/// <summary>The temporary profile of a browser this library started.</summary>
	internal string? UserDataDirectory => _userDataDirectory;

	/// <summary>
	/// Starts Chrome headless with a temporary profile and <c>--remote-debugging-port=0</c>, and connects to the port
	/// Chrome writes to <c>DevToolsActivePort</c>, so no port is ever guessed.
	/// </summary>
	public static async Task<Browser> LaunchAsync(BrowserLaunchOptions? options = null, CancellationToken cancellationToken = default)
	{
		options ??= new BrowserLaunchOptions();
		var executable = options.ExecutablePath;
		if (executable is null)
		{
			var location = new ChromeLocator().Locate();
			executable = location.Path ?? throw new BrowserLaunchException(location.Describe());
		}

		var userData = Directory.CreateTempSubdirectory("nullean-cdp-").FullName;
		var start = new ProcessStartInfo(executable)
		{
			UseShellExecute = false,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
		};
		foreach (var argument in Arguments(options, userData))
			start.ArgumentList.Add(argument);

		Process process;
		try
		{
			process = Process.Start(start) ?? throw new BrowserLaunchException($"Could not start {executable}.");
		}
		catch (System.ComponentModel.Win32Exception e)
		{
			TryDelete(userData);
			throw new BrowserLaunchException($"Could not start {executable}: {e.Message}");
		}

		// Chrome writes to both streams; drain them so it never blocks on a full pipe, and keep the tail for errors.
		var output = new ConcurrentQueue<string>();
		void Keep(object? sender, DataReceivedEventArgs e)
		{
			if (e.Data is null)
				return;
			output.Enqueue(e.Data);
			while (output.Count > 20)
				output.TryDequeue(out string? _);
		}

		process.ErrorDataReceived += Keep;
		process.OutputDataReceived += Keep;
		process.BeginErrorReadLine();
		process.BeginOutputReadLine();

		try
		{
			var url = await WaitForDevToolsAsync(process, userData, options.StartTimeout, output, cancellationToken).ConfigureAwait(false);
			var connection = await CdpConnection.ConnectAsync(url, cancellationToken).ConfigureAwait(false);
			return new Browser(connection, process, userData);
		}
		catch
		{
			Kill(process);
			process.Dispose();
			TryDelete(userData);
			throw;
		}
	}

	/// <summary>Connects to a browser that is already running, from its DevTools web socket URL.</summary>
	public static async Task<Browser> ConnectAsync(Uri webSocketUrl, CancellationToken cancellationToken = default) =>
		new(await CdpConnection.ConnectAsync(webSocketUrl, cancellationToken).ConfigureAwait(false), null, null);

	internal static IEnumerable<string> Arguments(BrowserLaunchOptions options, string userDataDirectory)
	{
		if (options.Headless)
			yield return "--headless=new";
		yield return "--remote-debugging-port=0";
		yield return "--user-data-dir=" + userDataDirectory;
		yield return "--no-first-run";
		yield return "--no-default-browser-check";
		yield return "--disable-background-networking";
		yield return "--disable-component-update";
		yield return "--disable-default-apps";
		yield return "--disable-extensions";
		yield return "--disable-sync";
		yield return "--metrics-recording-only";
		yield return "--mute-audio";
		yield return "--password-store=basic";
		yield return "--use-mock-keychain";
		foreach (var argument in options.Arguments)
			yield return argument;
		yield return "about:blank";
	}

	private static async Task<Uri> WaitForDevToolsAsync(
		Process process,
		string userData,
		TimeSpan timeout,
		ConcurrentQueue<string> output,
		CancellationToken cancellationToken
	)
	{
		var file = Path.Combine(userData, "DevToolsActivePort");
		var deadline = Stopwatch.StartNew();
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (TryReadDevToolsActivePort(file) is { } url)
				return url;
			if (process.HasExited)
			{
				// The exit can be seen before the last output lines reach the handlers; WaitForExit drains them.
				process.WaitForExit();
				throw new BrowserLaunchException(Failure($"Chrome exited with code {process.ExitCode} before opening its DevTools port.", output));
			}
			if (deadline.Elapsed > timeout)
				throw new BrowserLaunchException(Failure($"Chrome did not open its DevTools port within {timeout.TotalSeconds:0} seconds.", output));
			await Task.Delay(25, cancellationToken).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Reads <c>DevToolsActivePort</c>: the port on the first line, the browser's web socket path on the second.
	/// <c>null</c> while it is missing or half written.
	/// </summary>
	internal static Uri? TryReadDevToolsActivePort(string file)
	{
		string[] lines;
		try
		{
			lines = File.ReadAllLines(file);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return null;
		}

		return ParseDevToolsActivePort(lines);
	}

	internal static Uri? ParseDevToolsActivePort(IReadOnlyList<string> lines)
	{
		if (lines.Count < 2 || !int.TryParse(lines[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var port)
			|| port is <= 0 or > 65535 || !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal))
			return null;
		return new Uri($"ws://127.0.0.1:{port}{lines[1].Trim()}");
	}

	private static string Failure(string message, ConcurrentQueue<string> output)
	{
		var tail = output.ToArray();
		return tail.Length == 0 ? message : message + "\nLast output:\n  " + string.Join("\n  ", tail);
	}

	/// <summary>A new context: its own cookies, storage and cache, gone when it is disposed.</summary>
	public async Task<BrowserContext> NewContextAsync(CancellationToken cancellationToken = default)
	{
		var result = await Connection.SendAsync("Target.createBrowserContext", w => w.WriteBoolean("disposeOnDetach", true), cancellationToken: cancellationToken)
			.ConfigureAwait(false);
		return new BrowserContext(this, result.GetProperty("browserContextId").GetString()!);
	}

	/// <summary>The browser's product and version, e.g. <c>Chrome/131.0.6778.85</c>.</summary>
	public async Task<string> VersionAsync(CancellationToken cancellationToken = default)
	{
		var result = await Connection.SendAsync("Browser.getVersion", cancellationToken: cancellationToken).ConfigureAwait(false);
		return result.GetProperty("product").GetString() ?? string.Empty;
	}

	/// <summary>Closes the browser if this library started it, and removes its temporary profile.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 1)
			return;
		if (_process is not null)
		{
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				await Connection.SendAsync("Browser.close", cancellationToken: timeout.Token).ConfigureAwait(false);
			}
			catch (Exception e) when (e is CdpException or CdpDisconnectedException or OperationCanceledException)
			{
			}
		}

		await Connection.DisposeAsync().ConfigureAwait(false);
		if (_process is null)
			return;

		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
			await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			Kill(_process);
		}

		_process.Dispose();
		if (_userDataDirectory is not null)
			await DeleteWithRetriesAsync(_userDataDirectory).ConfigureAwait(false);
	}

	private static void Kill(Process process)
	{
		try
		{
			if (!process.HasExited)
				process.Kill(entireProcessTree: true);
		}
		catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
		{
		}
	}

	// Chrome's helpers can hold files in the profile for a moment after the main process exits, on Windows especially.
	private static async Task DeleteWithRetriesAsync(string directory)
	{
		for (var attempt = 0; attempt < 10; attempt++)
		{
			if (TryDelete(directory))
				return;
			await Task.Delay(100).ConfigureAwait(false);
		}
	}

	private static bool TryDelete(string directory)
	{
		try
		{
			if (Directory.Exists(directory))
				Directory.Delete(directory, recursive: true);
			return true;
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}
}

/// <summary>An isolated set of pages: cookies, storage and cache are its own.</summary>
public sealed class BrowserContext : IAsyncDisposable
{
	private readonly Browser _browser;
	private int _disposed;

	internal BrowserContext(Browser browser, string id)
	{
		_browser = browser;
		Id = id;
	}

	/// <summary>The protocol's <c>browserContextId</c>.</summary>
	public string Id { get; }

	/// <summary>Opens a blank page in this context and attaches to it.</summary>
	public async Task<Page> NewPageAsync(CancellationToken cancellationToken = default)
	{
		var connection = _browser.Connection;
		var created = await connection.SendAsync("Target.createTarget", w =>
		{
			w.WriteString("url", "about:blank");
			w.WriteString("browserContextId", Id);
		}, cancellationToken: cancellationToken).ConfigureAwait(false);
		var targetId = created.GetProperty("targetId").GetString()!;
		var attached = await connection.SendAsync("Target.attachToTarget", w =>
		{
			w.WriteString("targetId", targetId);
			w.WriteBoolean("flatten", true);
		}, cancellationToken: cancellationToken).ConfigureAwait(false);
		var page = new Page(connection, targetId, attached.GetProperty("sessionId").GetString()!);
		await page.InitializeAsync(cancellationToken).ConfigureAwait(false);
		return page;
	}

	/// <summary>Closes every page in the context and discards its data.</summary>
	public async ValueTask DisposeAsync()
	{
		if (Interlocked.Exchange(ref _disposed, 1) == 1 || _browser.Connection.IsClosed)
			return;
		try
		{
			await _browser.Connection.SendAsync("Target.disposeBrowserContext", w => w.WriteString("browserContextId", Id)).ConfigureAwait(false);
		}
		catch (Exception e) when (e is CdpException or CdpDisconnectedException)
		{
			// Already gone with the browser.
		}
	}
}

internal static class JsonElementExtensions
{
	public static string? StringOrNull(this JsonElement element, string property) =>
		element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
			? value.GetString()
			: null;
}
