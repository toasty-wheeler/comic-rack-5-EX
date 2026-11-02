using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using cYo.Common.ComponentModel;
using cYo.Projects.ComicRack.Engine.IO.Provider;

namespace cYo.Projects.ComicRack.Engine.Database
{
	// Adds books to the library as soon as they appear in a watched library folder.
	// Files are imported once they stopped growing and can be opened, so archives that are
	// still being copied or downloaded are not read half-written.
	public class LibraryFolderMonitor : DisposableObject
	{
		private class Pending
		{
			public bool IsFolder;

			public long Size = -1;

			public DateTime LastChange = DateTime.UtcNow;

			public DateTime Added = DateTime.UtcNow;
		}

		private static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(1.5);

		private static readonly TimeSpan GiveUpTime = TimeSpan.FromHours(2);

		private readonly Action<string> import;

		private readonly Dictionary<string, Pending> pending = new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);

		private readonly Timer timer;

		private HashSet<string> extensions;

		// import: called with a file or folder that should be scanned into the library
		public LibraryFolderMonitor(Action<string> import)
		{
			this.import = import;
			timer = new Timer(Tick, null, 1000, 1000);
			WatchFolder.FileArrived += WatchFolder_FileArrived;
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				WatchFolder.FileArrived -= WatchFolder_FileArrived;
				timer.Dispose();
			}
			base.Dispose(disposing);
		}

		private bool IsBookFile(string path)
		{
			if (extensions == null)
			{
				extensions = new HashSet<string>(Providers.Readers.GetFileExtensions(), StringComparer.OrdinalIgnoreCase);
			}
			return extensions.Contains(Path.GetExtension(path));
		}

		private void WatchFolder_FileArrived(object sender, FileSystemEventArgs e)
		{
			try
			{
				string path = e.FullPath;
				bool isFolder = Directory.Exists(path);
				if (!isFolder && !IsBookFile(path))
				{
					return;
				}
				lock (pending)
				{
					pending[path] = new Pending
					{
						IsFolder = isFolder
					};
				}
			}
			catch (Exception)
			{
			}
		}

		private void Tick(object state)
		{
			List<string> ready = new List<string>();
			lock (pending)
			{
				DateTime now = DateTime.UtcNow;
				foreach (KeyValuePair<string, Pending> item in pending.ToList())
				{
					string path = item.Key;
					Pending p = item.Value;
					try
					{
						if (p.IsFolder)
						{
							// A folder moved in; files copied into it report themselves
							if (!Directory.Exists(path))
							{
								pending.Remove(path);
							}
							else if (now - p.Added >= QuietTime)
							{
								pending.Remove(path);
								ready.Add(path);
							}
							continue;
						}
						FileInfo fi = new FileInfo(path);
						if (!fi.Exists || now - p.Added > GiveUpTime)
						{
							pending.Remove(path);
							continue;
						}
						if (fi.Length != p.Size)
						{
							p.Size = fi.Length;
							p.LastChange = now;
							continue;
						}
						if (now - p.LastChange >= QuietTime && p.Size > 0 && CanOpen(path))
						{
							pending.Remove(path);
							ready.Add(path);
						}
					}
					catch (Exception)
					{
						pending.Remove(path);
					}
				}
			}
			foreach (string path in ready)
			{
				try
				{
					import(path);
				}
				catch (Exception)
				{
				}
			}
		}

		// False while another program still has the file open for writing
		private static bool CanOpen(string path)
		{
			try
			{
				using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
				{
					return true;
				}
			}
			catch (IOException)
			{
				return false;
			}
			catch (UnauthorizedAccessException)
			{
				return false;
			}
		}
	}
}
