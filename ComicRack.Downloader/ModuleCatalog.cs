using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace cYo.Projects.ComicRack.Downloader
{
	public class SiteInfo
	{
		public string Domain { get; set; }

		public string Name { get; set; }

		public string ModuleFile { get; set; }

		public bool Adult { get; set; }
	}

	// Indexes the HDoujin Lua modules by the domains they register. The index is built by
	// scanning the source for Domains.Add(...) so no script has to run until a download starts.
	public class ModuleCatalog
	{
		private static readonly Regex domainRegex = new Regex(@"Domains\s*[.:]\s*Add\s*\(\s*(['""])([^'""]+)\1(?:\s*,\s*(['""])([^'""]*)\3)?", RegexOptions.Compiled);

		private static readonly Regex nameRegex = new Regex(@"module\.Name\s*=\s*(['""])([^'""]+)\1", RegexOptions.Compiled);

		private static readonly Regex adultRegex = new Regex(@"module\.Adult\s*=\s*true", RegexOptions.Compiled);

		private readonly List<SiteInfo> sites = new List<SiteInfo>();

		private readonly Dictionary<string, string> modulesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public IReadOnlyList<string> Folders { get; }

		public IReadOnlyList<SiteInfo> Sites => sites;

		public string GenericModuleFile { get; private set; }

		// Earlier folders win, so a user folder with updated modules overrides the bundled ones
		public ModuleCatalog(params string[] folders)
		{
			Folders = folders.Where(f => !string.IsNullOrEmpty(f) && Directory.Exists(f)).ToList();
			Load();
		}

		private void Load()
		{
			foreach (string folder in Folders)
			{
				if (GenericModuleFile == null && File.Exists(Path.Combine(folder, "Generic.lua")))
				{
					GenericModuleFile = Path.Combine(folder, "Generic.lua");
				}
				string luaFolder = Directory.Exists(Path.Combine(folder, "lua")) ? Path.Combine(folder, "lua") : folder;
				foreach (string file in Directory.GetFiles(luaFolder, "*.lua", SearchOption.AllDirectories))
				{
					string name = Path.GetFileNameWithoutExtension(file);
					if (modulesByName.ContainsKey(name) || name.Equals("Generic", StringComparison.OrdinalIgnoreCase))
					{
						continue;
					}
					modulesByName[name] = file;
					string source;
					try
					{
						source = File.ReadAllText(file);
					}
					catch (IOException)
					{
						continue;
					}
					string moduleName = nameRegex.Match(source) is Match nm && nm.Success ? nm.Groups[2].Value : name;
					bool adult = adultRegex.IsMatch(source);
					foreach (Match m in domainRegex.Matches(source))
					{
						sites.Add(new SiteInfo
						{
							Domain = m.Groups[2].Value.Trim().ToLowerInvariant(),
							Name = m.Groups[4].Success && m.Groups[4].Value.Length > 0 ? m.Groups[4].Value : moduleName,
							ModuleFile = file,
							Adult = adult
						});
					}
				}
			}
			sites.Sort((a, b) => string.Compare(a.Domain, b.Domain, StringComparison.OrdinalIgnoreCase));
		}

		public string FindModuleFile(string name)
		{
			name = Path.GetFileNameWithoutExtension(name ?? string.Empty);
			return modulesByName.TryGetValue(name, out string file) ? file : null;
		}

		private static string StripWww(string host)
		{
			return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : host;
		}

		// Most specific domain match first (e.g. "es.3hentai.net" before "3hentai.net")
		public IEnumerable<SiteInfo> FindSites(string url)
		{
			string host = StripWww(UrlUtility.Host(url).ToLowerInvariant());
			if (host.Length == 0)
			{
				return Enumerable.Empty<SiteInfo>();
			}
			return sites.Where(s =>
			{
				string d = StripWww(s.Domain);
				return host == d || host.EndsWith("." + d, StringComparison.Ordinal);
			}).OrderByDescending(s => s.Domain.Length).ToList();
		}

		public SiteInfo FindSite(string url)
		{
			return FindSites(url).FirstOrDefault();
		}
	}
}
