using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace cYo.Projects.ComicRack.Downloader
{
	public enum DownloadStatus
	{
		Queued,
		Running,
		Completed,
		Failed,
		Canceled
	}

	public enum OutputFormat
	{
		Cbz,
		Cb7,
		Cbt,
		Pdf,
		Folder
	}

	public class DownloadOptions
	{
		public string OutputFolder { get; set; }

		public OutputFormat Format { get; set; } = OutputFormat.Cbz;

		public int ParallelImages { get; set; } = 4;

		public int Retries { get; set; } = 3;

		// Keep going when single images fail instead of failing the whole gallery
		public bool SkipFailedPages { get; set; }

		public DownloadOptions Clone()
		{
			return (DownloadOptions)MemberwiseClone();
		}
	}

	public class DownloadJob
	{
		private readonly object sync = new object();

		private readonly StringBuilder log = new StringBuilder();

		private readonly List<string> outputFiles = new List<string>();

		private DownloadStatus status;

		private string statusText = "Queued";

		private string title;

		private float progress;

		public Guid Id { get; } = Guid.NewGuid();

		public string Url { get; }

		public DownloadOptions Options { get; }

		public string Site { get; internal set; }

		public string Error { get; internal set; }

		public DateTime Added { get; } = DateTime.Now;

		internal CancellationTokenSource Cancellation { get; private set; } = new CancellationTokenSource();

		public DownloadJob(string url, DownloadOptions options)
		{
			Url = url.Trim();
			Options = options;
			title = Url;
		}

		public event EventHandler Changed;

		public string Title
		{
			get => title;
			internal set
			{
				title = string.IsNullOrWhiteSpace(value) ? Url : value;
				OnChanged();
			}
		}

		public DownloadStatus Status
		{
			get => status;
			internal set
			{
				status = value;
				OnChanged();
			}
		}

		public string StatusText
		{
			get => statusText;
			internal set
			{
				statusText = value;
				OnChanged();
			}
		}

		// 0..1
		public float Progress
		{
			get => progress;
			internal set
			{
				progress = Math.Max(0f, Math.Min(1f, value));
				OnChanged();
			}
		}

		public bool IsFinished => status == DownloadStatus.Completed || status == DownloadStatus.Failed || status == DownloadStatus.Canceled;

		public string[] OutputFiles
		{
			get
			{
				lock (sync)
				{
					return outputFiles.ToArray();
				}
			}
		}

		public string Log
		{
			get
			{
				lock (sync)
				{
					return log.ToString();
				}
			}
		}

		internal void AddOutput(string file)
		{
			lock (sync)
			{
				outputFiles.Add(file);
			}
			OnChanged();
		}

		internal void WriteLog(string message)
		{
			lock (sync)
			{
				log.AppendLine($"[{DateTime.Now:HH:mm:ss}] {message}");
			}
		}

		public void Cancel()
		{
			Cancellation.Cancel();
		}

		// Prepares a finished job to run again
		internal void Reset()
		{
			Cancellation = new CancellationTokenSource();
			Error = null;
			lock (sync)
			{
				outputFiles.Clear();
			}
			progress = 0f;
			statusText = "Queued";
			Status = DownloadStatus.Queued;
		}

		private void OnChanged()
		{
			Changed?.Invoke(this, EventArgs.Empty);
		}
	}
}
