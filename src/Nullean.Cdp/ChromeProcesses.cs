using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Nullean.Cdp.Protocol;

namespace Nullean.Cdp;

/// <summary>
/// Keeps launched Chromes from outliving the process that started them. A normal exit (and Ctrl+C) kills every Chrome
/// still running. A process that is killed cannot clean up, so each profile records its owner, and the next launch
/// closes the Chromes whose owner is gone and deletes their profiles.
/// </summary>
internal static class ChromeProcesses
{
	public const string ProfilePrefix = "nullean-cdp-";
	public const string OwnerFile = "nullean-cdp-owner";

	private static readonly ConcurrentDictionary<int, (Process Chrome, string Profile)> Running = new();
	private static int Hooked;

	/// <summary>Records the owner in the profile and kills <paramref name="chrome"/> if this process exits first.</summary>
	public static void Track(Process chrome, string userDataDirectory)
	{
		if (Interlocked.Exchange(ref Hooked, 1) == 0)
			AppDomain.CurrentDomain.ProcessExit += (_, _) => KillAll();
		Running[chrome.Id] = (chrome, userDataDirectory);
		using var self = Process.GetCurrentProcess();
		File.WriteAllText(Path.Combine(userDataDirectory, OwnerFile), Owner(self.Id, self.StartTime));
	}

	public static void Untrack(Process chrome) => Running.TryRemove(chrome.Id, out _);

	internal static string Owner(int pid, DateTime started) =>
		$"pid={pid.ToString(CultureInfo.InvariantCulture)}\nstarted={started.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)}\n";

	/// <summary>
	/// Kills every Chrome this process started that is still running and deletes its profile. Never throws: it runs
	/// on exit.
	/// </summary>
	internal static void KillAll()
	{
		foreach (var (_, (chrome, profile)) in Running)
		{
			try
			{
				if (!chrome.HasExited)
				{
					chrome.Kill(entireProcessTree: true);
					chrome.WaitForExit(2000);
				}
			}
			catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
			{
			}

			for (var attempt = 0; attempt < 10 && Directory.Exists(profile); attempt++)
			{
				try
				{
					Directory.Delete(profile, recursive: true);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					Thread.Sleep(50);
				}
			}
		}
	}

	/// <summary>
	/// Closes the Chromes of profiles whose owner is no longer running, through each one's own DevTools port (so no
	/// unrelated process can be hit by a reused process id), and deletes the profiles. Profiles without an owner file
	/// are left alone.
	/// </summary>
	internal static async Task<int> ReapOrphansAsync(string? tempRoot = null)
	{
		var reaped = 0;
		string[] profiles;
		try
		{
			profiles = Directory.GetDirectories(tempRoot ?? Path.GetTempPath(), ProfilePrefix + "*");
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return 0;
		}

		foreach (var profile in profiles)
		{
			if (!IsOrphan(profile))
				continue;
			if (Browser.TryReadDevToolsActivePort(Path.Combine(profile, "DevToolsActivePort")) is { } url)
				await CloseAsync(url).ConfigureAwait(false);
			// Chrome writes to its profile while it shuts down; deleting before it is gone would leave a stray folder.
			await WaitForUnlockAsync(profile).ConfigureAwait(false);
			for (var attempt = 0; attempt < 10 && Directory.Exists(profile); attempt++)
			{
				try
				{
					Directory.Delete(profile, recursive: true);
				}
				catch (Exception e) when (e is IOException or UnauthorizedAccessException)
				{
					await Task.Delay(100).ConfigureAwait(false);
				}
			}

			if (!Directory.Exists(profile))
				reaped++;
		}

		return reaped;
	}

	// Chrome removes its profile lock (SingletonLock on macOS and Linux, lockfile on Windows) when it exits.
	private static async Task WaitForUnlockAsync(string profile)
	{
		var waited = Stopwatch.StartNew();
		while (waited.Elapsed < TimeSpan.FromSeconds(5) && IsLocked(profile))
			await Task.Delay(50).ConfigureAwait(false);

		static bool IsLocked(string profile)
		{
			try
			{
				return Directory.EnumerateFileSystemEntries(profile, "SingletonLock").Any()
					|| Directory.EnumerateFileSystemEntries(profile, "lockfile").Any();
			}
			catch (Exception e) when (e is IOException or UnauthorizedAccessException)
			{
				return false;
			}
		}
	}

	private static bool IsOrphan(string profile)
	{
		Dictionary<string, string> owner;
		try
		{
			owner = File.ReadAllLines(Path.Combine(profile, OwnerFile))
				.Select(l => l.Split('=', 2))
				.Where(p => p.Length == 2)
				.ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
		}
		catch (Exception e) when (e is IOException or UnauthorizedAccessException)
		{
			return false;
		}

		if (!owner.TryGetValue("pid", out var pidText) || !int.TryParse(pidText, NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
			|| !owner.TryGetValue("started", out var startedText) || !long.TryParse(startedText, NumberStyles.None, CultureInfo.InvariantCulture, out var started))
			return false;
		return !IsRunning(pid, started);
	}

	// The same process id with the same start time; a reused id belongs to a process that started later.
	private static bool IsRunning(int pid, long startedTicks)
	{
		try
		{
			using var process = Process.GetProcessById(pid);
			return Math.Abs(process.StartTime.ToUniversalTime().Ticks - startedTicks) < TimeSpan.TicksPerSecond;
		}
		catch (ArgumentException)
		{
			return false;
		}
		catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
		{
			// Not ours to inspect: assume it is running and leave its profile alone.
			return true;
		}
	}

	private static async Task CloseAsync(Uri url)
	{
		try
		{
			using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
			await using var connection = await CdpConnection.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
			await connection.SendAsync("Browser.close", cancellationToken: timeout.Token).ConfigureAwait(false);
		}
		catch (Exception e) when (e is CdpException or CdpDisconnectedException or OperationCanceledException
			or System.Net.WebSockets.WebSocketException or IOException)
		{
			// Already gone, or not a Chrome of ours on that port.
		}
	}
}
