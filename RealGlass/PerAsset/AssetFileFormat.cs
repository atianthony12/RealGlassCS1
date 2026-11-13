using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// The text of RealGlassAssets.xml: every building asset that has glass of its own, one element
    /// each, holding only what it overrides -- under the names those settings have in
    /// RealGlass.xml, written the way the sliders show them (a percentage as 47). Knows nothing
    /// about the game and makes no engine call: tools/test-settings.ps1 runs it on the built DLL.
    /// </summary>
    /// <remarks>
    /// One file for every city, like BOB's: an asset is the same everywhere, so its glass is too.
    /// Nothing of it goes into a save. Read and written as RealGlass.xml is (SettingsXmlFormat's
    /// numbers and comments, ColorText's colours, INVARIANT culture throughout).
    /// </remarks>
    public static class AssetFileFormat
    {
        public const string RootName = "RealGlassAssets";
        public const string AssetElement = "Asset";

        /// <summary>
        /// The root of RealGlassDefinition.xml: the glass an asset's CREATOR gave it, shipped next to its
        /// .crp (CreatorFile) -- the same Asset elements, named by the asset's own name ("Tower", what
        /// its creator typed when saving it), not by the name the game gives it in a city
        /// ("123456789.Tower_Data": the package is saved again under its Workshop id on upload).
        /// </summary>
        public const string DefinitionRoot = "RealGlassDefinition";

        /// <summary>The suffix the game gives a saved asset's prefab (SaveAssetPanel: assetName + "_Data").</summary>
        public const string DataSuffix = "_Data";

        /// <summary>Goes up when a name or a unit in the file changes meaning.</summary>
        public const int Version = 1;

        // The global settings' names (Settings.cs): the asset's own value of each.
        public const string StrengthName = "GlassStrength";
        public const string DetailName = "GlassPaintedDetail";
        public const string PresetName = "GlassPreset";
        public const string ColourName = "GlassColor";
        public const string ReflectivityName = "GlassReflectivity";
        public const string ClarityName = "GlassClarity";
        public const string OwnColourName = "GlassOwnColour";
        public const string ThresholdName = "GlassThreshold";
        public const string NightWindowsName = "GlassNightWindows";
        public const string DirtName = "GlassDirt";
        public const string InteriorsName = "InteriorsEnabled";
        public const string FollowIlluminationName = "InteriorsFollowIllumination";
        public const string LodGlassName = "LodGlassFromModel";

        /// <summary>
        /// Curtains or blinds in its rooms (true or false). No global setting has this name:
        /// RealGlass.xml's InteriorsCurtains is the share of windows, which an asset only takes or not.
        /// </summary>
        public const string CurtainsName = "InteriorsCurtainsEnabled";

        /// <summary>
        /// A prop asset's surface (PropRecord), 1.1.9: &lt;Prop name="..."&gt; beside the buildings'
        /// &lt;Asset&gt; elements, holding PropSurface (Glass or Water) and the buildings' glass names
        /// it shares (GlassStrength, GlassPreset, GlassColor, GlassReflectivity, GlassClarity,
        /// GlassThreshold from 0). Only RealGlassAssets.xml has them: a creator's file is a building's.
        /// </summary>
        public const string PropElement = "Prop";
        public const string PropSurfaceName = "PropSurface";

        /// <summary>
        /// A prop's own rows (1.1.9, round 3): polish (100 - Frosted), water's strength and ripples,
        /// the sun's glint -- percent each. (PropSoftEdges, a decal's, was taken out unreleased with
        /// the decals: a file that still has it gets the "ignored" line.)
        /// </summary>
        public const string PropPolishName = "PropPolish", PropWaterStrengthName = "PropWaterStrength",
                            PropRippleStrengthName = "PropRippleStrength", PropRippleSizeName = "PropRippleSize",
                            PropRippleSpeedName = "PropRippleSpeed", PropGlintName = "PropSunGlint";

        /// <summary>The surfaces' names in the file, by PropRecord value (append only; 3, Reflective, taken out unreleased).</summary>
        public const string PropGlassText = "Glass", PropWaterText = "Water";

        /// <summary>The sliders' ranges, as the file writes them: the global rows' (UI/SettingsCatalog.cs).</summary>
        public const float StrengthMax = 100f, DetailMax = 100f, ReflectivityMax = 60f, ClarityMax = 100f, OwnColourMax = 100f, DirtMax = 100f;

        /// <summary>
        /// Shininess needed, per asset: lower than the global row's 40 (an asset's glass may be
        /// painted dull, and the walls it pulls in are that asset's alone) -- the Asset tab's slider.
        /// </summary>
        public const float ThresholdMin = 10f, ThresholdMax = 95f;

        private const string NewLine = "\r\n";

        public static string Write(IList<AssetRecord> records, string header)
        {
            return Write(records, header, RootName);
        }

        /// <summary>The text of a file under <paramref name="root"/> (<see cref="RootName"/> or <see cref="DefinitionRoot"/>).</summary>
        public static string Write(IList<AssetRecord> records, string header, string root)
        {
            return Write(records, null, header, root);
        }

        /// <summary>RealGlassAssets.xml's text: the buildings' assets, then the props' (<paramref name="props"/> may be null).</summary>
        public static string Write(IList<AssetRecord> records, IList<PropRecord> props, string header)
        {
            return Write(records, props, header, RootName);
        }

        private static string Write(IList<AssetRecord> records, IList<PropRecord> props, string header, string root)
        {
            var sorted = new List<AssetRecord>(records);
            sorted.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            var text = new StringBuilder(4096);
            text.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>").Append(NewLine);

            if (!string.IsNullOrEmpty(header))
            {
                text.Append("<!--").Append(NewLine);
                foreach (string line in header.Split('\n'))
                    text.Append(("  " + SettingsXmlFormat.CommentSafe(line)).TrimEnd()).Append(NewLine);
                text.Append("-->").Append(NewLine);
            }

            text.Append('<').Append(root).Append(" version=\"")
                .Append(Version.ToString(CultureInfo.InvariantCulture)).Append("\">").Append(NewLine);

            foreach (AssetRecord record in sorted)
            {
                if (record == null || string.IsNullOrEmpty(record.Name) || record.IsEmpty)
                    continue;

                text.Append(NewLine).Append("  <").Append(AssetElement).Append(" name=\"").Append(Attribute(record.Name)).Append('"');
                if (!string.IsNullOrEmpty(record.Title))
                    text.Append(" title=\"").Append(Attribute(record.Title)).Append('"');
                text.Append('>').Append(NewLine);

                if (record.Strength.HasValue)
                    Element(text, StrengthName, SettingsXmlFormat.FormatNumber(record.Strength.Value * 100f));
                if (record.PaintedDetail.HasValue)
                    Element(text, DetailName, SettingsXmlFormat.FormatNumber(record.PaintedDetail.Value * 100f));
                if (record.Preset.HasValue)
                    Element(text, PresetName, record.Preset.Value.ToString(CultureInfo.InvariantCulture));
                if (record.Colour.HasValue)
                    Element(text, ColourName, ColorText.Format(record.Colour.Value));
                if (record.Reflectivity.HasValue)
                    Element(text, ReflectivityName, SettingsXmlFormat.FormatNumber(record.Reflectivity.Value * 100f));
                if (record.Clarity.HasValue)
                    Element(text, ClarityName, SettingsXmlFormat.FormatNumber(record.Clarity.Value * 100f));
                if (record.OwnColour.HasValue)
                    Element(text, OwnColourName, SettingsXmlFormat.FormatNumber(record.OwnColour.Value * 100f));
                if (record.Threshold.HasValue)
                    Element(text, ThresholdName, SettingsXmlFormat.FormatNumber(record.Threshold.Value * 100f));
                if (record.NightWindows.HasValue)
                    Element(text, NightWindowsName, SettingsXmlFormat.FormatBool(record.NightWindows.Value));
                if (record.Dirt.HasValue)
                    Element(text, DirtName, SettingsXmlFormat.FormatNumber(record.Dirt.Value * 100f));
                if (record.Interiors.HasValue)
                    Element(text, InteriorsName, SettingsXmlFormat.FormatBool(record.Interiors.Value));
                if (record.Curtains.HasValue)
                    Element(text, CurtainsName, SettingsXmlFormat.FormatBool(record.Curtains.Value));
                if (record.FollowIllumination.HasValue)
                    Element(text, FollowIlluminationName, SettingsXmlFormat.FormatBool(record.FollowIllumination.Value));
                if (record.LodGlass.HasValue)
                    Element(text, LodGlassName, SettingsXmlFormat.FormatBool(record.LodGlass.Value));

                text.Append("  </").Append(AssetElement).Append('>').Append(NewLine);
            }

            if (props != null)
            {
                var sortedProps = new List<PropRecord>(props);
                sortedProps.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (PropRecord prop in sortedProps)
                {
                    if (prop == null || string.IsNullOrEmpty(prop.Name) || prop.IsEmpty)
                        continue;

                    text.Append(NewLine).Append("  <").Append(PropElement).Append(" name=\"").Append(Attribute(prop.Name)).Append('"');
                    if (!string.IsNullOrEmpty(prop.Title))
                        text.Append(" title=\"").Append(Attribute(prop.Title)).Append('"');
                    text.Append('>').Append(NewLine);
                    Element(text, PropSurfaceName, prop.Surface.Value == PropRecord.Water ? PropWaterText : PropGlassText);
                    if (prop.Strength.HasValue)
                        Element(text, StrengthName, SettingsXmlFormat.FormatNumber(prop.Strength.Value * 100f));
                    if (prop.Preset.HasValue)
                        Element(text, PresetName, prop.Preset.Value.ToString(CultureInfo.InvariantCulture));
                    if (prop.Colour.HasValue)
                        Element(text, ColourName, ColorText.Format(prop.Colour.Value));
                    PercentElement(text, ReflectivityName, prop.Reflectivity);
                    PercentElement(text, ClarityName, prop.Clarity);
                    PercentElement(text, ThresholdName, prop.Threshold);
                    PercentElement(text, PropPolishName, prop.Polish);
                    PercentElement(text, PropWaterStrengthName, prop.WaterStrength);
                    PercentElement(text, PropRippleStrengthName, prop.RippleStrength);
                    PercentElement(text, PropRippleSizeName, prop.RippleSize);
                    PercentElement(text, PropRippleSpeedName, prop.RippleSpeed);
                    PercentElement(text, PropGlintName, prop.Glint);
                    text.Append("  </").Append(PropElement).Append('>').Append(NewLine);
                }
            }

            text.Append(NewLine).Append("</").Append(root).Append('>').Append(NewLine);
            return text.ToString();
        }

        private static void Element(StringBuilder text, string name, string value)
        {
            text.Append("    <").Append(name).Append('>').Append(value).Append("</").Append(name).Append('>').Append(NewLine);
        }

        /// <summary>A fraction written as the slider's percent, when set.</summary>
        private static void PercentElement(StringBuilder text, string name, float? value)
        {
            if (value.HasValue)
                Element(text, name, SettingsXmlFormat.FormatNumber(value.Value * 100f));
        }

        /// <summary>
        /// Every asset in the text, the last one winning where an asset is written twice. NEVER
        /// throws, whatever the text: a player edits this file by hand.
        /// </summary>
        /// <param name="error">
        /// Null when the whole file was read. Otherwise why it could not be (the parser's line and
        /// position), and the assets returned are the ones that could be SALVAGED one by one -- a
        /// typo in one asset loses that asset only. An empty file is read in full: no assets.
        /// </param>
        /// <remarks>
        /// A value that cannot be taken as written is reported in <paramref name="problems"/> and
        /// skipped, or pulled into its range. No DTD, no entities beyond XML's own five, no external
        /// anything (<see cref="SafeXml"/>).
        /// </remarks>
        public static List<AssetRecord> Parse(string text, out int version, List<string> problems, out string error)
        {
            return Parse(text, RootName, out version, problems, out error);
        }

        /// <summary>As <see cref="Parse(string, out int, List{string}, out string)"/>, for a file under <paramref name="rootName"/>.</summary>
        public static List<AssetRecord> Parse(string text, string rootName, out int version, List<string> problems, out string error)
        {
            return Parse(text, rootName, out version, problems, out error, null);
        }

        /// <summary>
        /// RealGlassAssets.xml with its props: the buildings' assets returned, every prop added to
        /// <paramref name="props"/> (the last of a name winning). Never throws, as the others.
        /// </summary>
        public static List<AssetRecord> Parse(string text, out int version, List<string> problems, out string error, List<PropRecord> props)
        {
            return Parse(text, RootName, out version, problems, out error, props);
        }

        /// <param name="props">Null: a file that holds no props (a creator's); a &lt;Prop&gt; there is said and ignored.</param>
        private static List<AssetRecord> Parse(string text, string rootName, out int version, List<string> problems, out string error,
                                               List<PropRecord> props)
        {
            version = 0;
            error = null;
            var found = new Found();
            var foundProps = props != null ? new FoundProps() : null;
            try
            {
                ParseInto(text, rootName, out version, problems, out error, found, foundProps);
            }
            finally
            {
                if (foundProps != null)
                    props.AddRange(foundProps.Records());
            }
            return found.Records();
        }

        private static void ParseInto(string text, string rootName, out int version, List<string> problems, out string error,
                                      Found found, FoundProps foundProps)
        {
            version = 0;
            error = null;

            try
            {
                if (text == null || text.Trim().Length == 0)
                    return;

                XmlElement root;
                if (!SafeXml.TryLoad(text, out root, out error))
                {
                    Salvage(text, rootName, found, foundProps, problems);
                    return;
                }

                if (root.Name != rootName)
                {
                    error = "the outermost element is <" + root.Name + ">, not <" + rootName + ">";
                    Salvage(text, rootName, found, foundProps, problems);
                    return;
                }

                int.TryParse(root.GetAttribute("version"), NumberStyles.Integer, CultureInfo.InvariantCulture, out version);
                foreach (XmlNode node in root.ChildNodes)
                {
                    var element = node as XmlElement;
                    if (element != null)
                        ReadElement(element, found, foundProps, problems);
                }
            }
            catch (Exception e)
            {
                // Nothing above should throw; if something does, the file is unreadable, not the mod broken.
                if (error == null)
                    error = e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>One element under the root: a building's asset, a prop (where the file holds them), or said and ignored.</summary>
        private static void ReadElement(XmlElement element, Found found, FoundProps foundProps, List<string> problems)
        {
            if (string.Equals(element.Name, AssetElement, StringComparison.OrdinalIgnoreCase))
            {
                ReadAsset(element, found, problems);
                return;
            }

            if (foundProps != null && string.Equals(element.Name, PropElement, StringComparison.OrdinalIgnoreCase))
            {
                ReadProp(element, foundProps, problems);
                return;
            }

            Problem(problems, "<" + element.Name + "> ignored: only <" + AssetElement + ">" +
                              (foundProps != null ? " and <" + PropElement + ">" : "") + " elements are read");
        }

        /// <summary>The props read so far, in the file's order, the last of a name winning.</summary>
        private sealed class FoundProps
        {
            private readonly Dictionary<string, PropRecord> _byName = new Dictionary<string, PropRecord>(StringComparer.Ordinal);
            private readonly List<string> _order = new List<string>();

            public void Add(PropRecord record, List<string> problems)
            {
                if (_byName.ContainsKey(record.Name))
                    Problem(problems, record.Name + ": written twice, the last one taken");
                else
                    _order.Add(record.Name);
                _byName[record.Name] = record;
            }

            public List<PropRecord> Records()
            {
                var records = new List<PropRecord>(_order.Count);
                foreach (string name in _order)
                    records.Add(_byName[name]);
                return records;
            }
        }

        private static void ReadProp(XmlElement element, FoundProps found, List<string> problems)
        {
            string name = element.GetAttribute("name").Trim();
            if (name.Length == 0)
            {
                Problem(problems, "a <" + PropElement + "> without a name ignored");
                return;
            }

            var record = new PropRecord(name);
            string title = element.GetAttribute("title").Trim();
            record.Title = title.Length > 0 ? title : null;
            foreach (XmlNode child in element.ChildNodes)
            {
                var setting = child as XmlElement;
                if (setting == null)
                    continue;

                string value = setting.InnerText.Trim();
                string where = name + ": " + setting.Name + " '" + value + "' ";
                if (Is(setting.Name, PropSurfaceName))
                {
                    int surface;
                    if (TryPropSurface(value, out surface))
                        record.Surface = surface;
                    else
                        Problem(problems, where + "ignored: not " + PropGlassText + " or " + PropWaterText);
                }
                else if (Is(setting.Name, StrengthName))
                    record.Strength = Percent(value, StrengthMax, where, problems);
                else if (Is(setting.Name, ReflectivityName))
                    record.Reflectivity = Percent(value, ReflectivityMax, where, problems);
                else if (Is(setting.Name, ClarityName))
                    record.Clarity = Percent(value, ClarityMax, where, problems);
                else if (Is(setting.Name, ThresholdName))
                    record.Threshold = Percent(value, 0f, PropRecord.ThresholdMax * 100f, where, problems);
                else if (Is(setting.Name, PropPolishName))
                    record.Polish = Percent(value, 100f, where, problems);
                else if (Is(setting.Name, PropWaterStrengthName))
                    record.WaterStrength = Percent(value, PropRecord.WaterStrengthMax * 100f, where, problems);
                else if (Is(setting.Name, PropRippleStrengthName))
                    record.RippleStrength = Percent(value, PropRecord.RippleStrengthMax * 100f, where, problems);
                else if (Is(setting.Name, PropRippleSizeName))
                    record.RippleSize = Percent(value, PropRecord.RippleSizeMin * 100f, PropRecord.RippleSizeMax * 100f, where, problems);
                else if (Is(setting.Name, PropRippleSpeedName))
                    record.RippleSpeed = Percent(value, PropRecord.RippleSpeedMax * 100f, where, problems);
                else if (Is(setting.Name, PropGlintName))
                    record.Glint = Percent(value, PropRecord.GlintMax * 100f, where, problems);
                else if (Is(setting.Name, ColourName))
                {
                    Color32 colour;
                    if (ColorText.TryParse(value, out colour))
                        record.Colour = colour;
                    else
                        Problem(problems, where + "ignored: not a colour (#RRGGBB, or r, g, b)");
                }
                else if (Is(setting.Name, PresetName))
                {
                    int preset;
                    if (TryPreset(value, out preset))
                        record.Preset = preset;
                    else
                        Problem(problems, where + "ignored: not a kind of glass (0 Custom, or 1 to " + GlassPresets.Count + ")");
                }
                else
                {
                    Problem(problems, name + ": <" + setting.Name + "> ignored: a prop can have " + PropSurfaceName + ", " + StrengthName + ", " +
                                      PresetName + ", " + ColourName + ", " + ReflectivityName + ", " + ClarityName + ", " +
                                      ThresholdName + ", " + PropPolishName + ", " + PropWaterStrengthName + ", " + PropRippleStrengthName + ", " +
                                      PropRippleSizeName + ", " + PropRippleSpeedName + " and " + PropGlintName);
                }
            }

            if (record.IsEmpty)
            {
                Problem(problems, name + ": a prop without " + PropSurfaceName + " " + PropGlassText + " or " + PropWaterText + " ignored");
                return;
            }
            found.Add(record, problems);
        }

        /// <summary>A prop's surface by its name in the file (Glass, Water) or its value (1, 2).</summary>
        public static bool TryPropSurface(string text, out int value)
        {
            value = PropRecord.Game;
            if (string.IsNullOrEmpty(text))
                return false;

            string t = text.Trim();
            if (string.Equals(t, PropGlassText, StringComparison.OrdinalIgnoreCase) || t == "1")
                value = PropRecord.Glass;
            else if (string.Equals(t, PropWaterText, StringComparison.OrdinalIgnoreCase) || t == "2")
                value = PropRecord.Water;
            return value != PropRecord.Game;
        }

        /// <summary>The assets read so far, in the file's order, the last of a name winning.</summary>
        private sealed class Found
        {
            private readonly Dictionary<string, AssetRecord> _byName = new Dictionary<string, AssetRecord>(StringComparer.Ordinal);
            private readonly List<string> _order = new List<string>();

            public void Add(AssetRecord record, List<string> problems)
            {
                if (_byName.ContainsKey(record.Name))
                    Problem(problems, record.Name + ": written twice, the last one taken");
                else
                    _order.Add(record.Name);
                _byName[record.Name] = record;
            }

            public List<AssetRecord> Records()
            {
                var records = new List<AssetRecord>(_order.Count);
                foreach (string name in _order)
                    records.Add(_byName[name]);
                return records;
            }
        }

        private static void ReadAsset(XmlElement element, Found found, List<string> problems)
        {
            string name = element.GetAttribute("name").Trim();
            if (name.Length == 0)
            {
                Problem(problems, "an <" + AssetElement + "> without a name ignored");
                return;
            }

            AssetRecord record = new AssetRecord(name);
            string title = element.GetAttribute("title").Trim();
            record.Title = title.Length > 0 ? title : null;
            foreach (XmlNode child in element.ChildNodes)
            {
                var setting = child as XmlElement;
                if (setting != null)
                    Read(record, setting.Name, setting.InnerText.Trim(), problems);
            }

            found.Add(record, problems);
        }

        /// <summary>
        /// A file that is not well-formed, read asset by asset: each &lt;Asset ...&gt; up to its
        /// &lt;/Asset&gt; (or the next &lt;Asset, or the end) is parsed on its own, closed for it
        /// where its end tag is missing. What still cannot be read is said, with its line. The props
        /// the same way, each &lt;Prop ...&gt; to its &lt;/Prop&gt;, where the file holds them.
        /// </summary>
        private static void Salvage(string text, string rootName, Found found, FoundProps foundProps, List<string> problems)
        {
            // "\b" after the name: a <PropSurface> inside a prop starts no piece of its own.
            string names = foundProps != null ? "(" + AssetElement + "|" + PropElement + ")" : "(" + AssetElement + ")";
            MatchCollection starts = Regex.Matches(text, @"<\s*" + names + @"\b", RegexOptions.IgnoreCase);
            for (int i = 0; i < starts.Count; i++)
            {
                int start = starts[i].Index;
                int end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
                string piece = text.Substring(start, end - start);
                string elementName = string.Equals(starts[i].Groups[1].Value, PropElement, StringComparison.OrdinalIgnoreCase) ? PropElement : AssetElement;

                Match close = Regex.Match(piece, @"</\s*" + elementName + @"\s*>", RegexOptions.IgnoreCase);
                bool closed = close.Success;
                if (closed)
                {
                    piece = piece.Substring(0, close.Index + close.Length);
                }
                else
                {
                    // The last asset of a file cut short: whatever closes the root is not its.
                    Match rootClose = Regex.Match(piece, @"</\s*" + rootName + @"\s*>", RegexOptions.IgnoreCase);
                    if (rootClose.Success)
                        piece = piece.Substring(0, rootClose.Index);
                }

                XmlElement element;
                string why;
                if (!SafeXml.TryLoad(piece, out element, out why) && (closed || !SafeXml.TryLoad(piece + "</" + elementName + ">", out element, out why)))
                {
                    Problem(problems, (elementName == PropElement ? "the prop" : "the asset") + " at line " + LineOf(text, start) +
                                      " could not be read (" + why + ")");
                    continue;
                }

                if (string.Equals(element.Name, AssetElement, StringComparison.OrdinalIgnoreCase))
                    ReadAsset(element, found, problems);
                else if (foundProps != null && string.Equals(element.Name, PropElement, StringComparison.OrdinalIgnoreCase))
                    ReadProp(element, foundProps, problems);
            }
        }

        private static int LineOf(string text, int index)
        {
            int line = 1;
            for (int i = 0; i < index && i < text.Length; i++)
            {
                if (text[i] == '\n')
                    line++;
            }
            return line;
        }

        private static void Read(AssetRecord record, string name, string value, List<string> problems)
        {
            string where = record.Name + ": " + name + " '" + value + "' ";
            if (Is(name, StrengthName))
                record.Strength = Percent(value, StrengthMax, where, problems);
            else if (Is(name, DetailName))
                record.PaintedDetail = Percent(value, DetailMax, where, problems);
            else if (Is(name, ReflectivityName))
                record.Reflectivity = Percent(value, ReflectivityMax, where, problems);
            else if (Is(name, ClarityName))
                record.Clarity = Percent(value, ClarityMax, where, problems);
            else if (Is(name, OwnColourName))
                record.OwnColour = Percent(value, OwnColourMax, where, problems);
            else if (Is(name, ThresholdName))
                record.Threshold = Percent(value, ThresholdMin, ThresholdMax, where, problems);
            else if (Is(name, DirtName))
                record.Dirt = Percent(value, DirtMax, where, problems);
            else if (Is(name, NightWindowsName))
            {
                bool on;
                if (SettingsXmlFormat.TryParseBool(value, out on))
                    record.NightWindows = on;
                else
                    Problem(problems, where + "ignored: not true or false");
            }
            else if (Is(name, InteriorsName))
            {
                bool on;
                if (SettingsXmlFormat.TryParseBool(value, out on))
                    record.Interiors = on;
                else
                    Problem(problems, where + "ignored: not true or false");
            }
            else if (Is(name, CurtainsName))
            {
                bool on;
                if (SettingsXmlFormat.TryParseBool(value, out on))
                    record.Curtains = on;
                else
                    Problem(problems, where + "ignored: not true or false");
            }
            else if (Is(name, FollowIlluminationName))
            {
                bool on;
                if (SettingsXmlFormat.TryParseBool(value, out on))
                    record.FollowIllumination = on;
                else
                    Problem(problems, where + "ignored: not true or false");
            }
            else if (Is(name, LodGlassName))
            {
                bool on;
                if (SettingsXmlFormat.TryParseBool(value, out on))
                    record.LodGlass = on;
                else
                    Problem(problems, where + "ignored: not true or false");
            }
            else if (Is(name, ColourName))
            {
                Color32 colour;
                if (ColorText.TryParse(value, out colour))
                    record.Colour = colour;
                else
                    Problem(problems, where + "ignored: not a colour (#RRGGBB, or r, g, b)");
            }
            else if (Is(name, PresetName))
            {
                int preset;
                if (TryPreset(value, out preset))
                    record.Preset = preset;
                else
                    Problem(problems, where + "ignored: not a kind of glass (0 Custom, or 1 to " + GlassPresets.Count + ")");
            }
            else
            {
                Problem(problems, record.Name + ": <" + name + "> ignored: an asset can have " + StrengthName + ", " + DetailName +
                                  ", " + PresetName + ", " + ColourName + ", " + ReflectivityName + ", " + ClarityName + ", " +
                                  OwnColourName + ", " + ThresholdName + ", " + NightWindowsName + ", " + DirtName + ", " + InteriorsName + ", " +
                                  CurtainsName + ", " + FollowIlluminationName + " and " + LodGlassName);
            }
        }

        // ---- a creator's file (RealGlassDefinition.xml) ---------------------------------------

        /// <summary>A saved asset's own name: "Tower" for "Tower_Data" (and for "Tower").</summary>
        public static string Stem(string assetName)
        {
            if (string.IsNullOrEmpty(assetName))
                return assetName ?? "";

            string name = assetName.Trim();
            return name.Length > DataSuffix.Length && name.EndsWith(DataSuffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - DataSuffix.Length)
                : name;
        }

        /// <summary>
        /// The asset's own name in the name the game gives it: "Tower" for "123456789.Tower_Data". The
        /// package is everything before the FIRST dot (PackageManager.FindAssetByName splits there); a
        /// name without one is the game's own asset, never a creator's: null.
        /// </summary>
        public static string OwnName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
                return null;

            int dot = fullName.IndexOf('.');
            return dot > 0 && dot < fullName.Length - 1 ? Stem(fullName.Substring(dot + 1)) : null;
        }

        /// <summary>An entry named <paramref name="entryName"/> is the asset whose own name is <paramref name="assetName"/>
        /// ("Tower" or "Tower_Data" for "Tower"; letter case aside: the file is written by hand too).</summary>
        public static bool SameAsset(string entryName, string assetName)
        {
            return entryName != null && assetName != null &&
                   string.Equals(Stem(entryName), Stem(assetName), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The creator's entry for the asset the game calls <paramref name="fullName"/>
        /// ("123456789.Tower_Data"): the one named by its own name ("Tower" or "Tower_Data"), else the
        /// one named by the full name. Null when the file has none.
        /// </summary>
        public static AssetRecord FindDefinition(IList<AssetRecord> entries, string fullName)
        {
            if (entries == null || fullName == null)
                return null;

            string own = OwnName(fullName);
            AssetRecord byFullName = null;
            for (int i = 0; i < entries.Count; i++)
            {
                AssetRecord e = entries[i];
                if (e == null || e.Name == null)
                    continue;
                if (own != null && SameAsset(e.Name, own))
                    return e;
                if (string.Equals(e.Name, fullName, StringComparison.OrdinalIgnoreCase))
                    byFullName = e;
            }
            return byFullName;
        }

        /// <summary>
        /// A creator's file with one asset's entry put in under its own name, or taken out when the
        /// record has nothing of its own; every other entry kept. <paramref name="existing"/>: the
        /// file's text, null for no file. <paramref name="error"/> is set when the existing text could
        /// not be read in full: the entries returned are the ones salvaged from it, and the caller
        /// keeps the old file aside before writing.
        /// </summary>
        public static string MergeDefinition(string existing, string assetName, AssetRecord record, string header,
                                             List<string> problems, out string error)
        {
            int version;
            List<AssetRecord> entries = Parse(existing, DefinitionRoot, out version, problems, out error);
            string own = Stem(assetName);
            entries.RemoveAll(e => SameAsset(e.Name, own));

            if (record != null && !record.IsEmpty)
            {
                AssetRecord put = record.Clone();
                put.Name = own;
                entries.Add(put);
            }

            return Write(entries, header, DefinitionRoot);
        }

        /// <summary>A kind of glass by its number, or by its English name ("Silver reflective").</summary>
        public static bool TryPreset(string text, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text))
                return false;

            // A whole number, however written ("5", "5.0").
            double number;
            if (SettingsXmlFormat.TryParseNumber(text, out number))
            {
                if (number != Math.Floor(number) || number < int.MinValue || number > int.MaxValue)
                    return false;

                int whole = (int)number;
                GlassPreset preset;
                if (whole == GlassPresets.Custom || GlassPresets.TryGet(whole, out preset))
                {
                    value = whole;
                    return true;
                }
                return false;
            }

            if (string.Equals(text.Trim(), "Custom", StringComparison.OrdinalIgnoreCase))
            {
                value = GlassPresets.Custom;
                return true;
            }

            for (int i = 0; i < GlassPresets.Count; i++)
            {
                GlassPreset preset = GlassPresets.At(i);
                if (string.Equals(text.Trim(), preset.LogName, StringComparison.OrdinalIgnoreCase))
                {
                    value = preset.Value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>A percentage as the slider shows it (0..max), stored as a fraction; null when unreadable.</summary>
        private static float? Percent(string value, float max, string where, List<string> problems)
        {
            return Percent(value, 0f, max, where, problems);
        }

        /// <summary>A percentage as the slider shows it (min..max), stored as a fraction; null when unreadable.</summary>
        private static float? Percent(string value, float min, float max, string where, List<string> problems)
        {
            double number;
            if (!SettingsXmlFormat.TryParseNumber(value, out number))
            {
                Problem(problems, where + "ignored: not a number (decimals take a dot: 0.5)");
                return null;
            }

            if (number < min || number > max)
            {
                double clamped = Math.Max(min, Math.Min(max, number));
                Problem(problems, where + "pulled into its range " + SettingsXmlFormat.FormatNumber(min) + " to " +
                                  SettingsXmlFormat.FormatNumber(max) + ": " + SettingsXmlFormat.FormatNumber(clamped));
                number = clamped;
            }

            return (float)(number / 100.0);
        }

        private static bool Is(string name, string wanted)
        {
            return string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase);
        }

        private static void Problem(List<string> problems, string text)
        {
            if (problems != null)
                problems.Add(text);
        }

        /// <summary>
        /// An attribute's text, escaped -- and without the characters XML 1.0 forbids outright (a
        /// control character in a title would make a file this mod then cannot read back).
        /// </summary>
        private static string Attribute(string text)
        {
            var clean = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r' || c == '\n' || c == '\t')
                {
                    clean.Append(' ');
                }
                else if (char.IsSurrogate(c))
                {
                    // Only a whole pair.
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        clean.Append(c).Append(text[i + 1]);
                        i++;
                    }
                }
                else if (c >= 0x20 && c != '￾' && c != '￿')
                {
                    clean.Append(c);
                }
            }

            return clean.ToString().Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }
    }
}
