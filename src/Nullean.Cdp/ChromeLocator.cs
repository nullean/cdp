using System.Runtime.InteropServices;

namespace Nullean.Cdp;

/// <summary>Where a Chrome executable was found, or every place that was tried.</summary>
/// <param name="Path">The executable, or <c>null</c> when none was found.</param>
/// <param name="Source">What found it: an environment variable, <c>install path</c> or <c>PATH</c>.</param>
/// <param name="Tried">Every candidate checked, in order, with what proposed it.</param>
public sealed record ChromeLocation(string? Path, string? Source, IReadOnlyList<(string Source, string Path)> Tried)
{
	/// <summary>Whether a Chrome executable was found.</summary>
	public bool Found => Path is not null;

	/// <summary>A plain-text description of the search, for failure messages.</summary>
	public string Describe()
	{
		if (Found)
			return $"Chrome: {Path} (from {Source})";
		var lines = new List<string> { "No Chrome found. Looked in:" };
		lines.AddRange(Tried.Select(t => $"  {t.Path}   ({t.Source})"));
		lines.Add($"Set {ChromeLocator.Variable} to a Chrome, Chromium or Edge executable.");
		return string.Join('\n', lines);
	}
}

/// <summary>
/// Finds a Chrome executable: <c>CDP_CHROME</c>, then <c>CHROME_PATH</c>, then the standard install locations for the
/// operating system, then <c>google-chrome</c>, <c>chromium</c> and <c>chrome</c> on the <c>PATH</c>.
/// </summary>
public sealed class ChromeLocator
{
	/// <summary>The environment variable that names the executable to use, ahead of everything else.</summary>
	public const string Variable = "CDP_CHROME";

	private static readonly string[] PathNames = ["google-chrome", "google-chrome-stable", "chromium", "chromium-browser", "chrome", "microsoft-edge"];

	private readonly Func<string, string?> _getVariable;
	private readonly Func<string, bool> _fileExists;
	private readonly OSPlatform _os;

	/// <summary>A locator over the real environment and file system.</summary>
	public ChromeLocator() : this(Environment.GetEnvironmentVariable, File.Exists, CurrentPlatform())
	{ }

	internal ChromeLocator(Func<string, string?> getVariable, Func<string, bool> fileExists, OSPlatform os)
	{
		_getVariable = getVariable;
		_fileExists = fileExists;
		_os = os;
	}

	/// <summary>Searches for Chrome and returns the first executable that exists.</summary>
	public ChromeLocation Locate()
	{
		var tried = new List<(string, string)>();
		foreach (var (source, path) in Candidates())
		{
			tried.Add((source, path));
			if (_fileExists(path))
				return new ChromeLocation(path, source, tried);
		}
		return new ChromeLocation(null, null, tried);
	}

	private IEnumerable<(string Source, string Path)> Candidates()
	{
		foreach (var variable in new[] { Variable, "CHROME_PATH" })
		{
			var value = _getVariable(variable);
			if (!string.IsNullOrWhiteSpace(value))
				yield return (variable, value.Trim());
		}

		foreach (var path in InstallPaths())
			yield return ("install path", path);

		var pathVariable = _getVariable("PATH");
		if (string.IsNullOrEmpty(pathVariable))
			yield break;
		var separator = _os == OSPlatform.Windows ? ';' : ':';
		var extension = _os == OSPlatform.Windows ? ".exe" : "";
		var directories = pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries);
		foreach (var name in PathNames)
		{
			foreach (var directory in directories)
				yield return ("PATH", Join(directory, name + extension));
		}
	}

	private IEnumerable<string> InstallPaths()
	{
		if (_os == OSPlatform.OSX)
		{
			string[] apps =
			[
				"Google Chrome.app/Contents/MacOS/Google Chrome",
				"Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing",
				"Chromium.app/Contents/MacOS/Chromium",
				"Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
			];
			foreach (var app in apps)
				yield return "/Applications/" + app;
			var home = _getVariable("HOME");
			if (!string.IsNullOrEmpty(home))
				foreach (var app in apps)
					yield return Join(home, "Applications/" + app);
		}
		else if (_os == OSPlatform.Windows)
		{
			foreach (var variable in new[] { "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA" })
			{
				var root = _getVariable(variable);
				if (string.IsNullOrEmpty(root))
					continue;
				yield return Join(root, @"Google\Chrome\Application\chrome.exe");
				yield return Join(root, @"Chromium\Application\chrome.exe");
				yield return Join(root, @"Microsoft\Edge\Application\msedge.exe");
			}
		}
		else
		{
			yield return "/usr/bin/google-chrome";
			yield return "/usr/bin/google-chrome-stable";
			yield return "/opt/google/chrome/chrome";
			yield return "/usr/bin/chromium";
			yield return "/usr/bin/chromium-browser";
			yield return "/snap/bin/chromium";
			yield return "/usr/bin/microsoft-edge";
		}
	}

	// The OS is a parameter, so join by its separator rather than the host's.
	private string Join(string directory, string relative)
	{
		var separator = _os == OSPlatform.Windows ? '\\' : '/';
		return directory.TrimEnd('/', '\\') + separator + relative.Replace(_os == OSPlatform.Windows ? '/' : '\\', separator);
	}

	private static OSPlatform CurrentPlatform() =>
		OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;
}
