using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using cYo.Projects.ComicRack.Engine;
using cYo.Projects.ComicRack.Engine.IO;
using cYo.Projects.ComicRack.Engine.IO.Provider;

namespace cYo.Projects.ComicRack.Downloader
{
	public static class ImageType
	{
		// Picks the file extension from the image signature, falling back to content type and URL
		public static string GetExtension(byte[] data, string contentType, string urlOrHint)
		{
			if (data != null && data.Length >= 12)
			{
				if (data[0] == 0xFF && data[1] == 0xD8)
				{
					return ".jpg";
				}
				if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
				{
					return ".png";
				}
				if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
				{
					return ".gif";
				}
				if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' && data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P')
				{
					return ".webp";
				}
				if (data[0] == 'B' && data[1] == 'M')
				{
					return ".bmp";
				}
				if (data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p')
				{
					string brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
					return brand.StartsWith("avi") ? ".avif" : ".heic";
				}
				if ((data[0] == 0xFF && data[1] == 0x0A) || (data[4] == 'J' && data[5] == 'X' && data[6] == 'L'))
				{
					return ".jxl";
				}
			}
			if (!string.IsNullOrEmpty(contentType) && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
			{
				string sub = contentType.Substring(6).Split(';')[0].Trim().ToLowerInvariant();
				return sub == "jpeg" ? ".jpg" : "." + sub;
			}
			if (data != null && data.Length > 0 && (contentType == null || !contentType.Contains("html")))
			{
				Match m = Regex.Match(urlOrHint ?? string.Empty, @"\.?(jpe?g|png|gif|webp|avif|bmp|jxl)(?:$|[?#])", RegexOptions.IgnoreCase);
				if (m.Success)
				{
					return "." + m.Groups[1].Value.ToLowerInvariant().Replace("jpeg", "jpg");
				}
			}
			return null;
		}
	}

	public static class ComicPackager
	{
		private static readonly Dictionary<OutputFormat, string> extensions = new Dictionary<OutputFormat, string>
		{
			{ OutputFormat.Cbz, ".cbz" },
			{ OutputFormat.Cb7, ".cb7" },
			{ OutputFormat.Cbt, ".cbt" },
			{ OutputFormat.Pdf, ".pdf" },
			{ OutputFormat.Folder, string.Empty }
		};

		public static string Pack(IList<string> images, GalleryInfo info, ChapterEntry chapter, DownloadOptions options)
		{
			if (images.Count == 0)
			{
				throw new InvalidOperationException("No images were downloaded");
			}
			ComicInfo comicInfo = CreateComicInfo(info, chapter, images.Count);
			string title = SafeFileName(FirstNonEmpty(info["Title"], UrlUtility.Host(info.Url), "Download"));
			string folder = string.IsNullOrWhiteSpace(options.OutputFolder) ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) : options.OutputFolder;
			string name = title;
			if (chapter != null)
			{
				// Series/Series - Chapter.cbz
				folder = Path.Combine(folder, title);
				name = SafeFileName(title + " - " + FirstNonEmpty(chapter.Title, "Chapter " + chapter.Chapter));
			}
			else if (!string.IsNullOrWhiteSpace(info["Series"]))
			{
				// A single chapter of a known series goes where the series' other chapters go
				string series = SafeFileName(info["Series"]);
				folder = Path.Combine(folder, series);
				if (!title.StartsWith(series, StringComparison.OrdinalIgnoreCase))
				{
					name = SafeFileName(series + " - " + title);
				}
			}
			Directory.CreateDirectory(folder);
			string target = UniquePath(Path.Combine(folder, name), extensions[options.Format]);

			switch (options.Format)
			{
				case OutputFormat.Folder:
					Directory.CreateDirectory(target);
					foreach (string image in images)
					{
						File.Copy(image, Path.Combine(target, Path.GetFileName(image)));
					}
					File.WriteAllBytes(Path.Combine(target, "ComicInfo.xml"), comicInfo.ToArray());
					break;
				case OutputFormat.Cbz:
					WriteCbz(images, comicInfo, target);
					break;
				default:
					// Let ComicRack's own writers produce the other formats
					Convert(images, target, options.Format, comicInfo);
					break;
			}
			return target;
		}

		// Feeds the downloaded image files to ComicRack's storage writers
		private class ImageFileProvider : IImageProvider
		{
			private readonly IList<string> files;

			public ImageFileProvider(IList<string> files, string source)
			{
				this.files = files;
				Source = source;
			}

			public bool IsSlow => false;

			public string Source { get; }

			public int Count => files.Count;

			public System.Drawing.Bitmap GetImage(int index)
			{
				return cYo.Common.Drawing.BitmapExtensions.BitmapFromBytes(GetByteImage(index));
			}

			public byte[] GetByteImage(int index)
			{
				return File.ReadAllBytes(files[index]);
			}

			public ExportImageContainer GetByteImageForExport(int index)
			{
				return new ExportImageContainer
				{
					Data = GetByteImage(index),
					NeedsToConvert = false
				};
			}

			public ProviderImageInfo GetImageInfo(int index)
			{
				return new ProviderImageInfo(index, Path.GetFileName(files[index]), new FileInfo(files[index]).Length);
			}

			public ThumbnailImage GetThumbnail(int index)
			{
				return ThumbnailImage.CreateFrom(GetByteImage(index));
			}

			public void Dispose()
			{
			}
		}

		private static void WriteCbz(IList<string> images, ComicInfo comicInfo, string target)
		{
			string temp = target + ".part";
			using (FileStream fs = File.Create(temp))
			using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
			{
				foreach (string image in images)
				{
					// Images are already compressed, storing them keeps the archive fast to read
					zip.CreateEntryFromFile(image, Path.GetFileName(image), CompressionLevel.NoCompression);
				}
				ZipArchiveEntry entry = zip.CreateEntry("ComicInfo.xml", CompressionLevel.Optimal);
				using (Stream s = entry.Open())
				{
					byte[] xml = comicInfo.ToArray();
					s.Write(xml, 0, xml.Length);
				}
			}
			if (File.Exists(target))
			{
				File.Delete(target);
			}
			File.Move(temp, target);
		}

		private static void Convert(IList<string> images, string target, OutputFormat format, ComicInfo comicInfo)
		{
			int id;
			switch (format)
			{
				case OutputFormat.Cb7:
					id = KnownFileFormats.CB7;
					break;
				case OutputFormat.Cbt:
					id = KnownFileFormats.CBT;
					break;
				default:
					id = KnownFileFormats.PDF;
					break;
			}
			using (ImageFileProvider provider = new ImageFileProvider(images, Path.GetDirectoryName(images[0])))
			using (StorageProvider writer = Providers.Writers.CreateFormatProvider(id))
			{
				if (writer == null)
				{
					throw new InvalidOperationException("Output format " + format + " is not available");
				}
				writer.Store(provider, comicInfo, target, new StorageSetting { FormatId = id });
			}
		}

		public static ComicInfo CreateComicInfo(GalleryInfo info, ChapterEntry chapter, int pageCount)
		{
			string title = info["Title"];
			ComicInfo ci = new ComicInfo
			{
				Series = FirstNonEmpty(info["Series"], title),
				Title = chapter != null ? chapter.Title : (string.IsNullOrEmpty(info["Series"]) ? string.Empty : title),
				AlternateSeries = FirstNonEmpty(info["OriginalTitle"], info["AlternativeTitle"]),
				Writer = info["Author"],
				Penciller = FirstNonEmpty(info["Artist"], info["Author"]),
				Teams = info["Circle"],
				Publisher = FirstNonEmpty(info["Publisher"], info["Magazine"]),
				Characters = info["Characters"],
				Tags = JoinNonEmpty(info["Tags"], info["Parody"]),
				Genre = FirstNonEmpty(info["Genres"], info["Type"]),
				Summary = FirstNonEmpty(info["Summary"], info["Description"]),
				Translator = FirstNonEmpty(info["Translator"], info["Scanlator"]),
				Web = chapter?.Url ?? info.Url,
				PageCount = pageCount,
				Notes = $"Downloaded with ComicRack from {info.Url} ({info.ModuleName}) on {DateTime.Now:yyyy-MM-dd}"
			};
			string language = FirstNonEmpty(chapter?.Language, info["Language"]).Split(',')[0].Trim();
			string iso = UrlUtility.LanguageId(language);
			if (!string.IsNullOrEmpty(iso))
			{
				ci.LanguageISO = iso;
			}
			string direction = info["ReadingDirection"].ToLowerInvariant();
			if (direction.Contains("rtl") || direction.Contains("right to left") || direction.Contains("right-to-left"))
			{
				ci.Manga = MangaYesNo.YesAndRightToLeft;
			}
			Match year = Regex.Match(info["DateReleased"], @"\b(19|20)\d{2}\b");
			if (year.Success)
			{
				ci.Year = int.Parse(year.Value, CultureInfo.InvariantCulture);
			}
			if (string.Equals(info["Adult"], "true", StringComparison.OrdinalIgnoreCase) || info.AdultSite)
			{
				ci.AgeRating = "Adults Only 18+";
			}
			if (chapter != null)
			{
				string number = FirstNonEmpty(chapter.Chapter, Regex.Match(chapter.Title ?? string.Empty, @"(?i)(?:ch(?:apter|\.)?|episode|ep\.?|#)\s*(\d+(?:\.\d+)?)").Groups[1].Value);
				if (string.IsNullOrEmpty(number))
				{
					number = Regex.Match(chapter.Title ?? string.Empty, @"\d+(?:\.\d+)?").Value;
				}
				ci.Number = number;
				if (int.TryParse(Regex.Match(chapter.Volume ?? string.Empty, @"\d+").Value, out int volume))
				{
					ci.Volume = volume;
				}
			}
			else if (!string.IsNullOrEmpty(info["Series"]))
			{
				// A single chapter of a series (e.g. sent from the browser): only explicit labels count,
				// so gallery titles that merely contain numbers aren't misread
				ci.Number = FirstNonEmpty(info["Chapter"], Regex.Match(title, @"(?i)(?:\bch(?:apter|\.)?|\bepisode|\bep\.?|#)\s*(\d+(?:\.\d+)?)").Groups[1].Value);
				string volumeText = FirstNonEmpty(info["Volume"], Regex.Match(title, @"(?i)(?:\bvol(?:ume|\.)?|\bseason)\s*(\d+)").Groups[1].Value);
				if (int.TryParse(Regex.Match(volumeText, @"\d+").Value, out int volume))
				{
					ci.Volume = volume;
				}
			}
			return ci;
		}

		private static string FirstNonEmpty(params string[] values)
		{
			return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
		}

		// Joins comma separated lists, dropping duplicates
		private static string JoinNonEmpty(params string[] values)
		{
			return string.Join(", ", values.Where(v => !string.IsNullOrWhiteSpace(v))
				.SelectMany(v => v.Split(','))
				.Select(v => v.Trim())
				.Where(v => v.Length > 0)
				.Distinct(StringComparer.OrdinalIgnoreCase));
		}

		public static string SafeFileName(string name)
		{
			char[] invalid = Path.GetInvalidFileNameChars();
			string s = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
			s = Regex.Replace(s, @"\s+", " ").Trim().TrimEnd('.', ' ');
			if (s.Length > 150)
			{
				s = s.Substring(0, 150).TrimEnd('.', ' ');
			}
			return s.Length == 0 ? "Download" : s;
		}

		private static string UniquePath(string pathWithoutExtension, string extension)
		{
			string path = pathWithoutExtension + extension;
			for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
			{
				path = $"{pathWithoutExtension} ({i}){extension}";
			}
			return path;
		}
	}
}
