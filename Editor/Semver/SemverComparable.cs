using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace hackebein.vpm.packager.editor.semver
{
    /// <summary>
    /// Minimal SemVer comparator to sort versions (used for picking "latest" from index caches).
    /// This is not a full semver library; it prefers numeric major/minor/patch and treats prerelease as lower precedence.
    /// </summary>
    internal static class SemverComparable
    {
        internal static int Compare(string a, string b)
        {
            if (ReferenceEquals(a, b)) return 0;
            a = (a ?? "").Trim();
            b = (b ?? "").Trim();
            if (a.Length == 0 && b.Length == 0) return 0;
            if (a.Length == 0) return -1;
            if (b.Length == 0) return 1;

            if (!TryParse(a, out var va))
                return string.CompareOrdinal(a, b);
            if (!TryParse(b, out var vb))
                return string.CompareOrdinal(a, b);

            var c = va.major.CompareTo(vb.major);
            if (c != 0) return c;
            c = va.minor.CompareTo(vb.minor);
            if (c != 0) return c;
            c = va.patch.CompareTo(vb.patch);
            if (c != 0) return c;

            // prerelease: absent > present
            var aPre = va.prerelease;
            var bPre = vb.prerelease;
            var aHas = aPre != null && aPre.Length > 0;
            var bHas = bPre != null && bPre.Length > 0;
            if (!aHas && !bHas) return 0;
            if (!aHas) return 1;
            if (!bHas) return -1;

            // Compare prerelease identifiers.
            var n = Math.Max(aPre.Length, bPre.Length);
            for (var i = 0; i < n; i++)
            {
                if (i >= aPre.Length) return -1;
                if (i >= bPre.Length) return 1;

                var ai = aPre[i];
                var bi = bPre[i];

                var aNum = int.TryParse(ai, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
                var bNum = int.TryParse(bi, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);
                if (aNum && bNum)
                {
                    c = an.CompareTo(bn);
                    if (c != 0) return c;
                }
                else if (aNum != bNum)
                {
                    // numeric identifiers have lower precedence than non-numeric
                    return aNum ? -1 : 1;
                }
                else
                {
                    c = string.CompareOrdinal(ai, bi);
                    if (c != 0) return c;
                }
            }

            return 0;
        }

        private static bool TryParse(string s, out Parsed v)
        {
            v = default;
            if (string.IsNullOrWhiteSpace(s)) return false;

            // Drop build metadata
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s.Substring(0, plus);

            string prerelease = null;
            var dash = s.IndexOf('-');
            if (dash >= 0)
            {
                prerelease = s.Substring(dash + 1);
                s = s.Substring(0, dash);
            }

            var parts = s.Split('.');
            if (parts.Length < 1 || parts.Length > 3) return false;
            if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var maj)) return false;
            var min = 0;
            var pat = 0;
            if (parts.Length >= 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out min)) return false;
            if (parts.Length >= 3 && !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out pat)) return false;

            v.major = maj;
            v.minor = min;
            v.patch = pat;
            v.prerelease = string.IsNullOrWhiteSpace(prerelease) ? Array.Empty<string>() : prerelease.Split('.').Select(x => x.Trim()).ToArray();
            return true;
        }

        private struct Parsed
        {
            public int major;
            public int minor;
            public int patch;
            public string[] prerelease;
        }
    }
}

