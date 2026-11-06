using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Overscan
{
    /// <summary>
    /// Overscan BR: the handful of URL patterns the ad blocker refuses on top of
    /// <see cref="AdHosts"/>. Same spirit as that list (one rule per line, in
    /// <c>adurls.txt</c>, embedded), with one extension: <c>*</c> matches any run
    /// of characters. Added for the P2P video loaders, which live on ordinary CDNs
    /// (jsdelivr, unpkg) where refusing the host would refuse everything.
    /// </summary>
    /// <remarks>
    /// Runs on the request thread for every request, so the list is built once,
    /// never written again, and kept short: each rule is a linear glob over the URL
    /// without its query. A few patterns cost microseconds, and the report's
    /// handler timing is where that shows.
    /// </remarks>
    internal sealed class AdUrls
    {
        public const string ResourceName = "Overscan.adurls.txt";

        private readonly List<string> _rules = new List<string>();

        public AdUrls(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = (raw ?? string.Empty).Trim();

                // A rule that is only stars would refuse every request there is.
                if (line.Length == 0 || line[0] == '#' || line.Replace("*", string.Empty).Length < 3)
                {
                    continue;
                }

                _rules.Add(line.ToLowerInvariant());
            }
        }

        public int Count
        {
            get { return _rules.Count; }
        }

        public static AdUrls LoadEmbedded()
        {
            Assembly assembly = typeof(AdUrls).Assembly;
            using (Stream stream = assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null)
                {
                    return new AdUrls(new string[0]);
                }

                List<string> lines = new List<string>();
                using (StreamReader reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lines.Add(line);
                    }
                }

                return new AdUrls(lines);
            }
        }

        /// <summary>The rule that refuses this URL, or null. Never throws.</summary>
        public string Match(string url)
        {
            if (string.IsNullOrEmpty(url) || _rules.Count == 0)
            {
                return null;
            }

            int cut = url.IndexOfAny(new[] { '?', '#' });
            string bare = (cut < 0 ? url : url.Substring(0, cut)).ToLowerInvariant();
            foreach (string rule in _rules)
            {
                if (Glob(rule, bare))
                {
                    return rule;
                }
            }

            return null;
        }

        /// <summary>Whole-string glob with <c>*</c> only, iterative (no recursion on a URL).</summary>
        internal static bool Glob(string pattern, string text)
        {
            int p = 0, t = 0, star = -1, mark = 0;
            while (t < text.Length)
            {
                if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = t;
                }
                else if (p < pattern.Length && pattern[p] == text[t])
                {
                    p++;
                    t++;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
            {
                p++;
            }

            return p == pattern.Length;
        }
    }
}
