using System;
using System.IO;
using System.IO.Compression;
using System.Net;

namespace cYo.Projects.ComicRack.Downloader
{
	public static class ModuleUpdater
	{
		public const string DefaultSource = "https://github.com/HDoujinDownloader/HDoujinDownloader/archive/refs/heads/master.zip";

		// Downloads the latest modules from the HDoujin Downloader repository into <folder>\lua.
		// Returns the number of installed modules.
		public static int Update(string folder, string source = DefaultSource)
		{
			string temp = Path.GetTempFileName();
			try
			{
				using (WebClient client = new WebClient())
				{
					client.Headers[HttpRequestHeader.UserAgent] = HttpSession.DefaultUserAgent;
					client.DownloadFile(source, temp);
				}
				string target = Path.Combine(folder, "lua");
				string staging = Path.GetFullPath(target + ".new");
				if (Directory.Exists(staging))
				{
					Directory.Delete(staging, recursive: true);
				}
				Directory.CreateDirectory(staging);
				int count = 0;
				using (ZipArchive zip = ZipFile.OpenRead(temp))
				{
					foreach (ZipArchiveEntry entry in zip.Entries)
					{
						string path = entry.FullName.Replace('\\', '/');
						int i = path.IndexOf("/modules/lua/", StringComparison.OrdinalIgnoreCase);
						if (i < 0 || !path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						string file = Path.GetFullPath(Path.Combine(staging, path.Substring(i + "/modules/lua/".Length)));
						if (!file.StartsWith(staging + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
						{
							continue;
						}
						Directory.CreateDirectory(Path.GetDirectoryName(file));
						entry.ExtractToFile(file, overwrite: true);
						count++;
					}
				}
				if (count == 0)
				{
					Directory.Delete(staging, recursive: true);
					throw new InvalidDataException("The download contained no modules");
				}
				if (Directory.Exists(target))
				{
					Directory.Delete(target, recursive: true);
				}
				Directory.Move(staging, target);
				return count;
			}
			finally
			{
				File.Delete(temp);
			}
		}
	}
}
