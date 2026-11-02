using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web;

namespace cYo.Projects.ComicRack.Downloader
{
	public static class UrlUtility
	{
		public static string Resolve(string relative, string baseUrl)
		{
			relative = (relative ?? string.Empty).Trim();
			if (relative.Length == 0)
			{
				return relative;
			}
			if (Uri.TryCreate(relative, UriKind.Absolute, out Uri abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
			{
				return abs.AbsoluteUri;
			}
			if (relative.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
			{
				return relative;
			}
			if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri b))
			{
				return relative.StartsWith("//") ? "https:" + relative : relative;
			}
			return Uri.TryCreate(b, relative, out Uri r) ? r.AbsoluteUri : relative;
		}

		public static string Root(string url)
		{
			return Uri.TryCreate(url, UriKind.Absolute, out Uri u) ? u.GetLeftPart(UriPartial.Authority) + "/" : string.Empty;
		}

		public static string Host(string url)
		{
			if (!url.Contains("://"))
			{
				url = "https://" + url.TrimStart('/');
			}
			return Uri.TryCreate(url, UriKind.Absolute, out Uri u) ? u.Host : string.Empty;
		}

		public static string Domain(string url)
		{
			string host = Host(url);
			return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host.Substring(4) : host;
		}

		public static string GetParameter(string url, string name)
		{
			int q = url.IndexOf('?');
			if (q < 0)
			{
				return string.Empty;
			}
			string query = url.Substring(q + 1);
			int hash = query.IndexOf('#');
			if (hash >= 0)
			{
				query = query.Substring(0, hash);
			}
			return HttpUtility.ParseQueryString(query)[name] ?? string.Empty;
		}

		public static string SetParameter(string url, string name, string value)
		{
			string fragment = string.Empty;
			int hash = url.IndexOf('#');
			if (hash >= 0)
			{
				fragment = url.Substring(hash);
				url = url.Substring(0, hash);
			}
			int q = url.IndexOf('?');
			string path = q < 0 ? url : url.Substring(0, q);
			List<string> parts = q < 0 ? new List<string>() : url.Substring(q + 1).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries).ToList();
			string encoded = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value ?? string.Empty);
			int index = parts.FindIndex(p => string.Equals(Uri.UnescapeDataString(p.Split('=')[0]), name, StringComparison.Ordinal));
			if (index >= 0)
			{
				parts[index] = encoded;
			}
			else
			{
				parts.Add(encoded);
			}
			return path + "?" + string.Join("&", parts) + fragment;
		}

		public static string StripParameters(string url)
		{
			int q = url.IndexOfAny(new[] { '?', '#' });
			return q < 0 ? url : url.Substring(0, q);
		}

		public static string Unescape(string s)
		{
			s = HttpUtility.HtmlDecode(s ?? string.Empty);
			s = Regex.Replace(s, @"\\u([0-9a-fA-F]{4})", m => ((char)int.Parse(m.Groups[1].Value, NumberStyles.HexNumber)).ToString());
			s = s.Replace("\\/", "/").Replace("\\\"", "\"");
			try
			{
				return Uri.UnescapeDataString(s);
			}
			catch (UriFormatException)
			{
				return s;
			}
		}

		public static string LanguageId(string name)
		{
			name = (name ?? string.Empty).Trim();
			if (name.Length == 0)
			{
				return string.Empty;
			}
			CultureInfo c = CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(ci =>
				string.Equals(ci.EnglishName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(ci.NativeName, name, StringComparison.OrdinalIgnoreCase) || string.Equals(ci.TwoLetterISOLanguageName, name, StringComparison.OrdinalIgnoreCase));
			return c?.TwoLetterISOLanguageName ?? string.Empty;
		}

		public static string LanguageName(string id)
		{
			try
			{
				return string.IsNullOrWhiteSpace(id) ? string.Empty : new CultureInfo(id.Trim().Split('-', '_')[0]).EnglishName;
			}
			catch (CultureNotFoundException)
			{
				return string.Empty;
			}
		}
	}
}
