using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.XPath;
using HtmlAgilityPack;
using Jint;
using Jint.Native;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace cYo.Projects.ComicRack.Downloader
{
	public static class HtmlApi
	{
		public static HtmlNode Parse(string html)
		{
			HtmlDocument doc = new HtmlDocument
			{
				OptionFixNestedTags = true,
				OptionAutoCloseOnEnd = true
			};
			doc.LoadHtml(html ?? string.Empty);
			return doc.DocumentNode;
		}

		// Evaluates XPath 1.0 with full attribute/text/function support (HtmlAgilityPack's own
		// SelectNodes can't return attribute values like "//img/@src")
		private static IEnumerable<XPathNavigator> Evaluate(HtmlNode node, string xpath, out string scalar)
		{
			scalar = null;
			if (node == null || string.IsNullOrWhiteSpace(xpath))
			{
				return Enumerable.Empty<XPathNavigator>();
			}
			object result;
			try
			{
				result = node.CreateNavigator().Evaluate(FixXPath(xpath));
			}
			catch (XPathException)
			{
				return Enumerable.Empty<XPathNavigator>();
			}
			if (result is XPathNodeIterator it)
			{
				List<XPathNavigator> list = new List<XPathNavigator>();
				while (it.MoveNext())
				{
					list.Add(it.Current.Clone());
				}
				return list;
			}
			scalar = Convert.ToString(result, CultureInfo.InvariantCulture);
			return Enumerable.Empty<XPathNavigator>();
		}

		// XPath 2.0 functions modules sometimes use, mapped onto XPath 1.0
		private static string FixXPath(string xpath)
		{
			xpath = Regex.Replace(xpath, @"ends-with\(\s*([^,]+?)\s*,\s*('[^']*'|""[^""]*"")\s*\)", m =>
				$"substring({m.Groups[1].Value}, string-length({m.Groups[1].Value}) - string-length({m.Groups[2].Value}) + 1) = {m.Groups[2].Value}");
			xpath = Regex.Replace(xpath, @"lower-case\(\s*([^)]+?)\s*\)", m =>
				$"translate({m.Groups[1].Value}, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')");
			return xpath;
		}

		public static IEnumerable<string> SelectValues(HtmlNode node, string xpath)
		{
			IEnumerable<XPathNavigator> navs = Evaluate(node, xpath, out string scalar);
			if (scalar != null)
			{
				return new[] { scalar.Trim() };
			}
			return navs.Select(nav =>
			{
				string v;
				if (nav.NodeType == XPathNodeType.Attribute)
				{
					v = nav.Value;
				}
				else
				{
					HtmlNode n = (nav as HtmlNodeNavigator)?.CurrentNode;
					v = n != null ? Text(n) : nav.Value;
				}
				return HtmlEntity.DeEntitize(v ?? string.Empty).Trim();
			}).ToList();
		}

		public static IEnumerable<HtmlNode> SelectNodes(HtmlNode node, string xpath)
		{
			return Evaluate(node, xpath, out _).Select(nav => (nav as HtmlNodeNavigator)?.CurrentNode).Where(n => n != null).ToList();
		}

		public static string Text(HtmlNode node)
		{
			if (node == null)
			{
				return string.Empty;
			}
			return HtmlEntity.DeEntitize(node.InnerText ?? string.Empty).Trim();
		}
	}

	public static class JsonApi
	{
		public static JToken Parse(string text)
		{
			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}
			try
			{
				using (JsonTextReader reader = new JsonTextReader(new StringReader(text)) { DateParseHandling = DateParseHandling.None, FloatParseHandling = FloatParseHandling.Decimal })
				{
					return JToken.ReadFrom(reader);
				}
			}
			catch (JsonException)
			{
				return null;
			}
		}

		public static IEnumerable<JToken> Select(JToken token, string path)
		{
			if (token == null)
			{
				return Enumerable.Empty<JToken>();
			}
			if (string.IsNullOrEmpty(path) || path == "$")
			{
				return new[] { token };
			}
			try
			{
				return token.SelectTokens(path, errorWhenNoMatch: false).ToList();
			}
			catch (JsonException)
			{
				return Enumerable.Empty<JToken>();
			}
		}

		public static IEnumerable<JToken> Children(JToken token)
		{
			if (token is JObject o)
			{
				return o.Properties().Select(p => p.Value);
			}
			if (token is JArray a)
			{
				return a;
			}
			return Enumerable.Empty<JToken>();
		}

		public static int Count(JToken token)
		{
			switch (token)
			{
				case null:
					return 0;
				case JContainer c:
					return c.Count;
				default:
					return ToText(token).Length > 0 ? 1 : 0;
			}
		}

		public static JToken Index(JToken token, object key)
		{
			try
			{
				if (token is JArray a && key is int i)
				{
					return i >= 0 && i < a.Count ? a[i] : null;
				}
				if (token is JObject o)
				{
					return o[Convert.ToString(key, CultureInfo.InvariantCulture)];
				}
			}
			catch (Exception)
			{
			}
			return null;
		}

		public static string ToText(JToken token)
		{
			switch (token)
			{
				case null:
					return string.Empty;
				case JValue v:
					switch (v.Type)
					{
						case JTokenType.Null:
						case JTokenType.Undefined:
							return string.Empty;
						case JTokenType.Boolean:
							return (bool)v.Value ? "true" : "false";
						default:
							return Convert.ToString(v.Value, CultureInfo.InvariantCulture);
					}
				default:
					return token.ToString(Formatting.None);
			}
		}
	}

	public class JavaScriptEngine
	{
		private readonly Jint.Engine engine;

		public JavaScriptEngine(string userAgent)
		{
			engine = new Jint.Engine(o => o.LimitRecursion(512).TimeoutInterval(TimeSpan.FromSeconds(30)).MaxStatements(100_000_000));
			// Minimal browser environment many sites' inline scripts expect
			engine.Execute("var window = this; var self = this; var globalThis = this; var document = { cookie: '', getElementById: function() { return null; }, querySelector: function() { return null; }, write: function() {} }; var location = { href: '', hostname: '', protocol: 'https:' }; var navigator = { userAgent: " + JsonConvert.ToString(userAgent ?? string.Empty) + " }; var atob = function(s) { return __atob(s); };");
			engine.SetValue("__atob", new Func<string, string>(s => System.Text.Encoding.GetEncoding("ISO-8859-1").GetString(Convert.FromBase64String(s))));
		}

		public JsValue Execute(string code)
		{
			return engine.Evaluate(code ?? string.Empty);
		}

		public static string ToText(object value)
		{
			if (!(value is JsValue v) || v.IsUndefined() || v.IsNull())
			{
				return string.Empty;
			}
			if (v.IsString())
			{
				return v.AsString();
			}
			if (v.IsObject())
			{
				return ToJson(v);
			}
			return v.ToString();
		}

		public static string ToJson(object value)
		{
			if (!(value is JsValue v) || v.IsUndefined())
			{
				return string.Empty;
			}
			if (v.IsString())
			{
				// Already serialized ("JSON.stringify(...)")
				return v.AsString();
			}
			Jint.Engine e = new Jint.Engine();
			e.SetValue("__v", v);
			return e.Evaluate("JSON.stringify(__v)").ToString();
		}

		// Unpacks "eval(function(p,a,c,k,e,d){...})" style packed scripts
		public static string Deobfuscate(string code)
		{
			if (string.IsNullOrWhiteSpace(code))
			{
				return string.Empty;
			}
			Match m = Regex.Match(code, @"eval\s*\((function\s*\(p,\s*a,\s*c,\s*k,\s*e,\s*[dr]\)[\s\S]+)\)\s*;?\s*$");
			if (!m.Success)
			{
				return code;
			}
			try
			{
				return new Jint.Engine(o => o.TimeoutInterval(TimeSpan.FromSeconds(10))).Evaluate("(" + m.Groups[1].Value + ")").ToString();
			}
			catch (Exception)
			{
				return code;
			}
		}
	}
}
