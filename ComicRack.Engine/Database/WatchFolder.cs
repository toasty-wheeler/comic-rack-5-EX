using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.Serialization;
using System.Xml.Serialization;
using cYo.Common.ComponentModel;

namespace cYo.Projects.ComicRack.Engine.Database
{
	[Serializable]
	public class WatchFolder : DisposableObject
	{
		private string folder;

		private bool watch = true;

		[NonSerialized]
		private FileSystemWatcher fileSystemWatcher;

		// Raised (on a thread pool thread) when a file or folder is created in, moved into or
		// renamed inside any watched library folder
		public static event EventHandler<FileSystemEventArgs> FileArrived;

		[XmlAttribute]
		public string Folder
		{
			get
			{
				return folder;
			}
			set
			{
				if (!(folder == value))
				{
					if (value == null)
					{
						throw new ArgumentNullException();
					}
					folder = value;
					UpdateWatcher(watch);
				}
			}
		}

		// Watched folders pick up new books automatically (like a music player's library folder)
		[DefaultValue(true)]
		[XmlAttribute]
		public bool Watch
		{
			get
			{
				return watch;
			}
			set
			{
				if (watch != value)
				{
					watch = value;
					UpdateWatcher(watch);
				}
			}
		}

		public FileSystemWatcher FileSystemWatcher
		{
			get
			{
				if (fileSystemWatcher == null)
				{
					fileSystemWatcher = new FileSystemWatcher
					{
						// Copying many books at once can overflow the default 8KB buffer
						InternalBufferSize = 64 * 1024,
						NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
					};
					fileSystemWatcher.Created += Watcher_Arrived;
					fileSystemWatcher.Renamed += Watcher_Arrived;
				}
				return fileSystemWatcher;
			}
		}

		public WatchFolder()
		{
		}

		public WatchFolder(string path, bool watch)
		{
			this.watch = watch;
			Folder = path;
		}

		[OnDeserialized]
		private void OnDeserialized(StreamingContext context)
		{
			UpdateWatcher();
		}

		private void Watcher_Arrived(object sender, FileSystemEventArgs e)
		{
			FileArrived?.Invoke(this, e);
		}

		private void UpdateWatcher(bool watch)
		{
			try
			{
				FileSystemWatcher.EnableRaisingEvents = false;
				FileSystemWatcher.IncludeSubdirectories = true;
				FileSystemWatcher.Path = folder;
				FileSystemWatcher.EnableRaisingEvents = watch;
			}
			catch (Exception)
			{
				FileSystemWatcher.EnableRaisingEvents = false;
			}
		}

		private void UpdateWatcher()
		{
			UpdateWatcher(watch);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing && fileSystemWatcher != null)
			{
				UpdateWatcher(watch: false);
				fileSystemWatcher.Created -= Watcher_Arrived;
				fileSystemWatcher.Renamed -= Watcher_Arrived;
				fileSystemWatcher.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
