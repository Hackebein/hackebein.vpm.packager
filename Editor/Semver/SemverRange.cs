using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace hackebein.vpm.packager.editor.semver
{
    /// <summary>
    /// Small SemVer range parser/validator intended for validating VPM-style dependency ranges.
    /// Supports common forms: "*", "3.5.x", "^1.2.3", "~1.2.3", ">=1.0.0 <2.0.0-a", "1.2.3 - 2.0.0", "A || B".
    /// </summary>
    internal static class SemverRange
    {
        internal static bool AnySatisfies(string range, IEnumerable<string> versions)
        {
            if (string.IsNullOrWhiteSpace(range)) return false;
            if (versions == null) return false;

            foreach (var v in versions)
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                if (Satisfies(range, v.Trim()))
                    return true;
            }

            return false;
        }

        internal static bool IsValid(string range)
        {
            return TryParse(range, out _);
        }

        internal static bool TryParse(string range, out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(range))
            {
                error = "Empty range.";
                return false;
            }

            var clauses = range.Split(new[] { "||" }, StringSplitOptions.None)
                .Select(c => (c ?? "").Trim())
                .ToArray();

            if (clauses.Length == 0)
            {
                error = "Empty range.";
                return false;
            }

            foreach (var clause in clauses)
            {
                if (clause.Length == 0)
                {
                    error = "Empty range clause.";
                    return false;
                }

                if (!TryParseClause(clause))
                {
                    error = $"Invalid range clause: \"{clause}\"";
                    return false;
                }
            }

            return true;
        }

        private static bool TryParseClause(string clause)
        {
            clause = (clause ?? "").Trim();
            if (clause.Length == 0) return false;

            // Anything / wildcard
            if (clause == "*" || clause.Equals("x", StringComparison.OrdinalIgnoreCase))
                return true;

            // Hyphen range: "1.2.3 - 2.0.0"
            if (clause.Contains(" - ", StringComparison.Ordinal))
            {
                var parts = clause.Split(new[] { " - " }, StringSplitOptions.None);
                if (parts.Length != 2) return false;
                return IsVersionSpec(parts[0]) && IsVersionSpec(parts[1]);
            }

            // Tokenize by whitespace. We support:
            // - ">=1.0.0 <2.0.0"
            // - ">= 1.0.0 < 2.0.0"
            // - "^1.2.3" or "^ 1.2.3"
            // - "~1.2.3" or "~ 1.2.3"
            var tokens = clause.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;

            if (tokens[0] == "^" || tokens[0] == "~")
            {
                if (tokens.Length != 2) return false;
                return IsVersionSpec(tokens[1]);
            }

            if (tokens.Length == 1)
            {
                var t = tokens[0];
                if (t.StartsWith("^", StringComparison.Ordinal) || t.StartsWith("~", StringComparison.Ordinal))
                    return IsVersionSpec(t.Substring(1));

                // comparator-in-token (e.g. ">=1.0.0")
                if (TryParseComparatorToken(t, out var verInToken))
                    return IsVersionSpec(verInToken);

                // bare version or wildcard version (e.g. "3.5.x")
                return IsVersionSpec(t);
            }

            // Multi-token: comparator sets
            var i = 0;
            while (i < tokens.Length)
            {
                var token = tokens[i];

                if (TryParseComparatorToken(token, out var versionPart))
                {
                    if (!IsVersionSpec(versionPart)) return false;
                    i++;
                    continue;
                }

                if (IsComparatorOperatorToken(token))
                {
                    if (i + 1 >= tokens.Length) return false;
                    if (!IsVersionSpec(tokens[i + 1])) return false;
                    i += 2;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool Satisfies(string range, string version)
        {
            var clauses = range.Split(new[] { "||" }, StringSplitOptions.None)
                .Select(c => (c ?? "").Trim())
                .Where(c => c.Length > 0)
                .ToArray();

            foreach (var clause in clauses)
            {
                if (SatisfiesClause(clause, version))
                    return true;
            }

            return false;
        }

        private static bool SatisfiesClause(string clause, string version)
        {
            clause = (clause ?? "").Trim();
            if (clause.Length == 0) return false;

            if (clause == "*" || clause.Equals("x", StringComparison.OrdinalIgnoreCase))
                return true;

            // Hyphen range: "1.2.3 - 2.0.0"
            if (clause.Contains(" - ", StringComparison.Ordinal))
            {
                var parts = clause.Split(new[] { " - " }, StringSplitOptions.None);
                if (parts.Length != 2) return false;
                var lo = ExpandLowerBound(parts[0]);
                var hiExcl = ExpandUpperBoundExclusive(parts[1]);
                return Compare(version, lo) >= 0 && Compare(version, hiExcl) < 0;
            }

            var tokens = clause.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return false;

            // ^ / ~ forms
            if (tokens.Length == 1 && (tokens[0].StartsWith("^", StringComparison.Ordinal) || tokens[0].StartsWith("~", StringComparison.Ordinal)))
            {
                var op = tokens[0][0];
                var baseVer = tokens[0].Substring(1).Trim();
                return SatisfiesCaretTilde(op, baseVer, version);
            }
            if (tokens.Length == 2 && (tokens[0] == "^" || tokens[0] == "~"))
            {
                return SatisfiesCaretTilde(tokens[0][0], tokens[1], version);
            }

            // Single token: exact or wildcard
            if (tokens.Length == 1)
            {
                var t = tokens[0];

                if (TryParseComparatorToken(t, out var vpart))
                {
                    var (cmp, rhs) = ParseComparator(t);
                    return CompareWith(version, cmp, rhs);
                }

                if (WildcardVersion.TryParse(t) && !SemverVersion.TryParse(t, out _))
                {
                    // wildcard/partial version spec -> translate to >=lower <upper
                    var lo = ExpandLowerBound(t);
                    var hiExcl = ExpandUpperBoundExclusive(t);
                    return Compare(version, lo) >= 0 && Compare(version, hiExcl) < 0;
                }

                // exact version
                return Compare(version, t) == 0;
            }

            // Multi-token comparator set: all comparators must match.
            var i = 0;
            while (i < tokens.Length)
            {
                var token = tokens[i];
                if (TryParseComparatorToken(token, out var _))
                {
                    var (cmp, rhs) = ParseComparator(token);
                    if (!CompareWith(version, cmp, rhs)) return false;
                    i++;
                    continue;
                }

                if (IsComparatorOperatorToken(token))
                {
                    if (i + 1 >= tokens.Length) return false;
                    var op = token;
                    var rhs = tokens[i + 1];
                    if (!CompareWith(version, op, rhs)) return false;
                    i += 2;
                    continue;
                }

                return false;
            }

            return true;
        }

        private static bool SatisfiesCaretTilde(char op, string baseVersion, string version)
        {
            baseVersion = (baseVersion ?? "").Trim();
            if (baseVersion.Length == 0) return false;

            // If caret/tilde is used with a wildcard/partial version, treat it like the wildcard itself.
            if (WildcardVersion.TryParse(baseVersion) && !SemverVersion.TryParse(baseVersion, out _))
            {
                var loW = ExpandLowerBound(baseVersion);
                var hiW = ExpandUpperBoundExclusive(baseVersion);
                return Compare(version, loW) >= 0 && Compare(version, hiW) < 0;
            }

            var lo = ExpandLowerBound(baseVersion);
            var hi = op == '^' ? ExpandCaretUpperExclusive(lo) : ExpandTildeUpperExclusive(lo);
            return Compare(version, lo) >= 0 && Compare(version, hi) < 0;
        }

        private static (string cmp, string rhs) ParseComparator(string token)
        {
            token = (token ?? "").Trim();
            var ops = new[] { ">=", "<=", ">", "<", "=" };
            foreach (var op in ops)
            {
                if (token.StartsWith(op, StringComparison.Ordinal))
                    return (op, token.Substring(op.Length).Trim());
            }
            return ("=", token);
        }

        private static bool CompareWith(string version, string cmp, string rhsSpec)
        {
            rhsSpec = (rhsSpec ?? "").Trim();
            if (rhsSpec == "*" || rhsSpec.Equals("x", StringComparison.OrdinalIgnoreCase))
                return true;

            // wildcard rhs: interpret as interval
            if (WildcardVersion.TryParse(rhsSpec) && !SemverVersion.TryParse(rhsSpec, out _))
            {
                var lo = ExpandLowerBound(rhsSpec);
                var hi = ExpandUpperBoundExclusive(rhsSpec);
                switch (cmp)
                {
                    case "=":
                        return Compare(version, lo) >= 0 && Compare(version, hi) < 0;
                    case ">=":
                        return Compare(version, lo) >= 0;
                    case ">":
                        return Compare(version, hi) >= 0;
                    case "<":
                        return Compare(version, lo) < 0;
                    case "<=":
                        return Compare(version, lo) < 0; // best-effort
                    default:
                        return false;
                }
            }

            var rhs = ExpandLowerBound(rhsSpec);
            var c = Compare(version, rhs);
            switch (cmp)
            {
                case "=": return c == 0;
                case ">": return c > 0;
                case ">=": return c >= 0;
                case "<": return c < 0;
                case "<=": return c <= 0;
                default: return false;
            }
        }

        private static int Compare(string a, string b)
        {
            return SemverComparable.Compare(a, b);
        }

        private static string ExpandLowerBound(string versionSpec)
        {
            versionSpec = (versionSpec ?? "").Trim();
            if (versionSpec == "*" || versionSpec.Equals("x", StringComparison.OrdinalIgnoreCase))
                return "0.0.0";

            if (versionSpec.IndexOf('-') >= 0 || versionSpec.IndexOf('+') >= 0)
                return versionSpec;

            var parts = versionSpec.Split('.');
            if (parts.Length == 1) return parts[0] + ".0.0";
            if (parts.Length == 2) return parts[0] + "." + parts[1] + ".0";
            return versionSpec;
        }

        private static string ExpandUpperBoundExclusive(string versionSpec)
        {
            versionSpec = (versionSpec ?? "").Trim();
            if (versionSpec == "*" || versionSpec.Equals("x", StringComparison.OrdinalIgnoreCase))
                return "999999.0.0";

            var parts = versionSpec.Split('.').Select(p => p.Trim()).ToArray();
            if (parts.Length == 1)
            {
                if (!TryParseIntOrWildcard(parts[0], out var maj, out var majWild) || majWild) return "999999.0.0";
                return (maj + 1) + ".0.0";
            }
            if (parts.Length == 2)
            {
                TryParseIntOrWildcard(parts[0], out var maj, out var majWild);
                TryParseIntOrWildcard(parts[1], out var min, out var minWild);
                if (majWild) return "999999.0.0";
                if (minWild) return (maj + 1) + ".0.0";
                return maj + "." + (min + 1) + ".0";
            }
            if (parts.Length >= 3)
            {
                TryParseIntOrWildcard(parts[0], out var maj, out var majWild);
                TryParseIntOrWildcard(parts[1], out var min, out var minWild);
                TryParseIntOrWildcard(parts[2], out var pat, out var patWild);
                if (majWild) return "999999.0.0";
                if (minWild) return (maj + 1) + ".0.0";
                if (patWild) return maj + "." + (min + 1) + ".0";
                return maj + "." + min + "." + (pat + 1);
            }

            return versionSpec;
        }

        private static string ExpandCaretUpperExclusive(string loFull)
        {
            loFull = ExpandLowerBound(loFull);
            var p = loFull.Split('.');
            var maj = int.Parse(p[0], CultureInfo.InvariantCulture);
            var min = int.Parse(p[1], CultureInfo.InvariantCulture);
            var pat = int.Parse(p[2], CultureInfo.InvariantCulture);

            if (maj > 0) return (maj + 1) + ".0.0";
            if (min > 0) return "0." + (min + 1) + ".0";
            return "0.0." + (pat + 1);
        }

        private static string ExpandTildeUpperExclusive(string loFull)
        {
            loFull = ExpandLowerBound(loFull);
            var p = loFull.Split('.');
            var maj = int.Parse(p[0], CultureInfo.InvariantCulture);
            var min = int.Parse(p[1], CultureInfo.InvariantCulture);
            return maj + "." + (min + 1) + ".0";
        }

        private static bool TryParseIntOrWildcard(string token, out int value, out bool isWildcard)
        {
            value = 0;
            isWildcard = false;
            token = (token ?? "").Trim();
            if (token == "*" || token.Equals("x", StringComparison.OrdinalIgnoreCase))
            {
                isWildcard = true;
                return true;
            }

            return int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool IsVersionSpec(string token)
        {
            token = (token ?? "").Trim();
            if (token.Length == 0) return false;

            // Global wildcard
            if (token == "*" || token.Equals("x", StringComparison.OrdinalIgnoreCase))
                return true;

            // If it has pre-release/build markers, it must be a full SemVer (wildcards not allowed there).
            if (token.IndexOf('-') >= 0 || token.IndexOf('+') >= 0)
                return SemverVersion.TryParse(token, out _);

            // Exact SemVer (3-part) OR wildcard/partial forms (e.g. "3", "3.5", "3.5.x")
            if (SemverVersion.TryParse(token, out _))
                return true;

            return WildcardVersion.TryParse(token);
        }

        private static bool TryParseComparatorToken(string token, out string versionPart)
        {
            versionPart = null;
            token = (token ?? "").Trim();
            if (token.Length == 0) return false;

            var ops = new[] { ">=", "<=", ">", "<", "=" };
            foreach (var op in ops)
            {
                if (!token.StartsWith(op, StringComparison.Ordinal)) continue;
                versionPart = token.Substring(op.Length).Trim();
                return versionPart.Length > 0;
            }

            return false;
        }

        private static bool IsComparatorOperatorToken(string token)
        {
            switch ((token ?? "").Trim())
            {
                case ">":
                case ">=":
                case "<":
                case "<=":
                case "=":
                    return true;
                default:
                    return false;
            }
        }

        private readonly struct SemverVersion
        {
            public static bool TryParse(string s, out object unused)
            {
                unused = null;
                s = (s ?? "").Trim();
                if (s.Length == 0) return false;

                // split build
                string build = null;
                var plus = s.IndexOf('+');
                if (plus >= 0)
                {
                    build = s.Substring(plus + 1);
                    s = s.Substring(0, plus);
                    if (build.Length == 0) return false;
                    if (!AreDotIdentifiersValid(build, prerelease: false)) return false;
                }

                // split prerelease
                string pre = null;
                var dash = s.IndexOf('-');
                if (dash >= 0)
                {
                    pre = s.Substring(dash + 1);
                    s = s.Substring(0, dash);
                    if (pre.Length == 0) return false;
                    if (!AreDotIdentifiersValid(pre, prerelease: true)) return false;
                }

                var parts = s.Split('.');
                if (parts.Length != 3) return false;
                if (!TryParseNonNegativeInt(parts[0], out _)) return false;
                if (!TryParseNonNegativeInt(parts[1], out _)) return false;
                if (!TryParseNonNegativeInt(parts[2], out _)) return false;

                return true;
            }

            private static bool AreDotIdentifiersValid(string s, bool prerelease)
            {
                var ids = s.Split('.');
                if (ids.Length == 0) return false;
                foreach (var id in ids)
                {
                    if (string.IsNullOrEmpty(id)) return false;
                    if (!IsIdentifierValid(id, prerelease)) return false;
                }

                return true;
            }

            private static bool IsIdentifierValid(string id, bool prerelease)
            {
                // prerelease identifiers: [0-9A-Za-z-]+, numeric identifiers must not include leading zeros.
                // build identifiers: [0-9A-Za-z-]+, leading zeros allowed.
                for (var i = 0; i < id.Length; i++)
                {
                    var c = id[i];
                    if (char.IsLetterOrDigit(c) || c == '-') continue;
                    return false;
                }

                if (!prerelease) return true;

                var isNumeric = id.All(char.IsDigit);
                if (isNumeric && id.Length > 1 && id[0] == '0')
                    return false;
                return true;
            }

            private static bool TryParseNonNegativeInt(string s, out int value)
            {
                value = 0;
                if (string.IsNullOrEmpty(s)) return false;
                if (s.Length > 1 && s[0] == '0') return false;
                return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
            }
        }

        private static class WildcardVersion
        {
            public static bool TryParse(string token)
            {
                token = (token ?? "").Trim();
                if (token.Length == 0) return false;

                if (token == "*" || token.Equals("x", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (token.IndexOf('-') >= 0 || token.IndexOf('+') >= 0)
                    return false;

                var parts = token.Split('.');
                if (parts.Length == 0 || parts.Length > 3) return false;

                var wildcardSeen = false;
                foreach (var part in parts)
                {
                    if (part == "*" || part.Equals("x", StringComparison.OrdinalIgnoreCase))
                    {
                        wildcardSeen = true;
                        continue;
                    }

                    if (wildcardSeen)
                    {
                        // Don't allow "1.x.3" style specs.
                        return false;
                    }

                    if (!TryParseNonNegativeInt(part, out _))
                        return false;
                }

                return true;
            }

            private static bool TryParseNonNegativeInt(string s, out int value)
            {
                value = 0;
                if (string.IsNullOrEmpty(s)) return false;
                if (s.Length > 1 && s[0] == '0') return false;
                return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
            }
        }
    }
}

