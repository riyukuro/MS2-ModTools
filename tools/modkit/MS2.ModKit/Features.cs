// Which features are on for a target? The game decides it from two tables of its Xml.m2d and two settings:
//   table/feature_setting.xml   <setting type="<server type>" <LOCALE>="<level>" .../>   the level of a server type per locale
//   table/feature.xml           <feature name="<name>" <LOCALE>="<level>" .../>          the level a feature switches on at
// A feature is on when its level for the locale is not 0 and not above the level of the server type. The server type
// compares without letter case, the locale attribute is the locale name as it is ("KR", "NA").
//
// Rows that carry feature="..." and locale="..." (the <environment> rows of a map, for one) are picked like this:
// a row is out when its locale is set and is not the target's, or when its feature is not on (no feature = on).
// Of the rows left, the one whose feature has the higher level wins (no feature = level 0; equal levels: the name
// that sorts later); with the same feature the row of the target's locale wins over the row without a locale.
//
// The server reads the same two tables with its own rule (the data parser it is built on), and it differs:
//   - the server type compares with letter case, and a level that is not written counts as 0;
//   - a feature at level 0 is on (0 is not above any level); for the game such a feature does not exist;
//   - a row's locale compares without letter case, a feature name with it;
//   - of the rows left, the rows of the server's locale go before the rows without a locale, whatever their
//     features; then a row with a feature goes before a row without one (of those the last), then the higher level; equal
//     levels: the first.
// The locales the server knows are TW, TH, NA, CN, JP and KR.
using System;
using System.Collections.Generic;
using System.Globalization;
using MS2.ModKit.Xml;

namespace MS2.ModKit
{
    /// <summary>The features of one target: locale, server type and the level tables read from its data.</summary>
    public sealed class FeatureSet
    {
        public const string SettingTable = "table/feature_setting.xml", FeatureTable = "table/feature.xml";
        public const string DefaultServerType = "Live";

        public readonly string Locale, ServerType;
        public readonly int Level;
        /// <summary>True: the rule of the server's data parser; false: the rule of the game.</summary>
        public readonly bool ServerRule;
        readonly Dictionary<string, int> levels;
        static readonly string[] ServerLocales = { "TW", "TH", "NA", "CN", "JP", "KR" };

        FeatureSet(string locale, string serverType, int level, bool serverRule)
        {
            Locale = locale; ServerType = serverType; Level = level; ServerRule = serverRule;
            levels = new Dictionary<string, int>(serverRule ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        }

        // the level a row has for a locale; false when it cannot be read
        static bool LevelAttr(XmlElem row, string locale, bool serverRule, out int level)
        {
            string text = row.Attr(locale);
            level = 0;
            if (text == null && serverRule) return true;
            return int.TryParse(text ?? "", NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out level);
        }

        /// <summary>"NA/Live", "KR", "/Dev" -> locale and server type; a part left out stays null.</summary>
        public static void ParseOption(string text, out string locale, out string serverType)
        {
            locale = serverType = null;
            string t = (text ?? "").Trim();
            if (t.Length == 0) return;
            string[] parts = t.Split('/');
            if (parts.Length > 2) throw new ModKitException("features setting \"" + text + "\": write LOCALE/TYPE, for example NA/Live");
            if (parts[0].Trim().Length > 0) locale = parts[0].Trim();
            if (parts.Length == 2 && parts[1].Trim().Length > 0) serverType = parts[1].Trim();
        }

        /// <summary>
        /// The feature set the two tables give for a locale and a server type; null when it cannot be known: a table is
        /// missing or unreadable, or no setting row of that type has a level for the locale. With serverRule the
        /// tables are read as the server reads them (see the head of this file).
        /// </summary>
        public static FeatureSet Read(byte[] settingXml, byte[] featureXml, string locale, string serverType, bool serverRule = false)
        {
            if (settingXml == null || featureXml == null || string.IsNullOrEmpty(locale) || string.IsNullOrEmpty(serverType)) return null;
            if (serverRule && Array.IndexOf(ServerLocales, locale) < 0) return null;
            XmlDoc setting, feature;
            try
            {
                setting = XmlDoc.Parse(settingXml, SettingTable);
                feature = XmlDoc.Parse(featureXml, FeatureTable);
            }
            catch (ModKitException) { return null; }
            if (setting.Root == null || feature.Root == null) return null;
            FeatureSet set = null;
            foreach (XmlElem row in setting.Root.Children)
            {
                if (row.Name != "setting" || !string.Equals(row.Attr("type") ?? "", serverType, serverRule ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) continue;
                int level;
                if (!LevelAttr(row, locale, serverRule, out level)) return null;
                set = new FeatureSet(locale, serverType, level, serverRule);
                break;
            }
            if (set == null) return null;
            foreach (XmlElem row in feature.Root.Children)
            {
                string name = row.Name == "feature" ? row.Attr("name") : null;
                int level;
                if (string.IsNullOrEmpty(name)) continue;
                if (!LevelAttr(row, locale, serverRule, out level))
                {
                    if (serverRule) return null;        // the server stops on such a table
                    continue;
                }
                if (level == 0 && !serverRule) continue;
                set.levels[name] = level;
            }
            return set;
        }

        /// <summary>The level a feature switches on at; 0 for no feature and for a name the table does not have.</summary>
        public int LevelOf(string feature)
        {
            int level;
            return !string.IsNullOrEmpty(feature) && levels.TryGetValue(feature, out level) ? level : 0;
        }

        /// <summary>No feature is always on; a name the table does not have (for this locale) is off.</summary>
        public bool IsOn(string feature)
        {
            int level;
            if (string.IsNullOrEmpty(feature)) return true;
            return levels.TryGetValue(feature, out level) && level <= Level;
        }

        /// <summary>May the game take a row with this feature and locale at all?</summary>
        public bool Allows(string feature, string locale)
        {
            if (!string.IsNullOrEmpty(locale) && !string.Equals(locale, Locale, ServerRule ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
            return IsOn(feature);
        }

        /// <summary>Of two rows the game may take: does the second one win over the first?</summary>
        public bool SecondWins(string featureA, string localeA, string featureB, string localeB)
        {
            featureA = featureA ?? ""; featureB = featureB ?? ""; localeA = localeA ?? ""; localeB = localeB ?? "";
            if (ServerRule)
            {
                if ((localeA.Length > 0) != (localeB.Length > 0)) return localeB.Length > 0;
                if (featureA.Length == 0) return true;      // a row without a feature stays only until any other row comes
                return featureB.Length > 0 && LevelOf(featureB) > LevelOf(featureA);
            }
            if (!string.Equals(featureA, featureB, StringComparison.Ordinal))
            {
                int a = LevelOf(featureA), b = LevelOf(featureB);
                if (a != b) return b > a;
                return string.CompareOrdinal(featureA, featureB) <= 0;
            }
            if (string.Equals(localeA, localeB, StringComparison.Ordinal)) return false;
            if (localeA == Locale) return false;
            if (localeB == Locale) return true;
            if (localeA.Length == 0) return false;
            return localeB.Length == 0;
        }

        public string Text { get { return "locale " + Locale + ", server type " + ServerType + ", level " + Level.ToString(CultureInfo.InvariantCulture); } }
    }
}
