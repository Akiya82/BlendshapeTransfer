using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>A text the core produces for the window: a key of the language files and its arguments.</summary>
    internal readonly struct Message
    {
        public readonly string Key;
        public readonly object[] Args;

        public Message(string key, params object[] args)
        {
            Key = key;
            Args = args ?? Array.Empty<object>();
        }

        public override string ToString() => $"{Key}({string.Join(", ", Args)})";
    }

    /// <summary>Keys of the messages the core produces; their text is in Editor/Localization/en.json.</summary>
    internal static class Msg
    {
        public const string PlayMode = "error.playMode";
        public const string NoBody = "error.noBody";
        public const string InAsset = "error.inAsset";
        public const string NoAvatar = "error.noAvatar";
        public const string NoGarments = "error.noGarments";
        public const string GarmentEmpty = "error.garmentEmpty";
        public const string GarmentIsBody = "error.garmentIsBody";
        public const string NotUnderAvatar = "error.notUnderAvatar";
        public const string ListedTwice = "error.listedTwice";
        public const string SameData = "error.sameData";
        public const string SamePath = "error.samePath";
        public const string FitConflict = "error.fitConflict";
        public const string LostSource = "error.lostSource";
        public const string Unreadable = "error.unreadable";
        public const string Exception = "error.exception";
        public const string EditsDropped = "warning.editsDropped";
        public const string Singular = "warning.singular";
        public const string UnmatchedBones = "warning.unmatchedBones";
        public const string FitWeight = "info.fitWeight";
        public const string SkipKept = "skip.kept";
        public const string SkipNoReach = "skip.noReach";
    }

    /// <summary>
    /// The window's strings from Editor/Localization/&lt;code&gt;.json, one flat object of key → text each; a key's tooltip is under
    /// key + ".tip". Lookup goes chosen language → English → the key itself. The choice is an editor preference, English by default.
    /// </summary>
    internal static class Localization
    {
        public static readonly (string Code, string Name)[] Languages = { ("en", "English"), ("ru", "Русский"), ("ja", "日本語"), ("zh-Hans", "中文") };
        private const string PrefKey = "Akiya82.BlendshapeTransfer.Language";
        private const string Asmdef = "dev.akiya82.blendshapetransfer.editor";
        /// <summary>The language tests draw in; when set, the preference is neither read nor written.</summary>
        internal static string Override;
        /// <summary>Where tests keep their own language files.</summary>
        internal static string FolderOverride;
        /// <summary>Called with every key that has no English text, or whose English text does not fit its arguments; tests collect them.</summary>
        internal static Action<string> Missing;
        private static readonly Dictionary<string, Dictionary<string, string>> tables = new Dictionary<string, Dictionary<string, string>>();
        /// <summary>Problems already logged; Reload keeps them, so a broken file is logged once per domain load.</summary>
        private static readonly HashSet<string> reported = new HashSet<string>();
        private static string folder;
        private static string current;

        public static string Current
        {
            get
            {
                if (Override != null) return Override;
                if (current != null) return current;
                var code = EditorPrefs.GetString(PrefKey, "en");
                return current = Languages.Any(l => l.Code == code) ? code : "en";
            }
            set
            {
                if (value == Current) return;
                if (Override != null) Override = value;
                else EditorPrefs.SetString(PrefKey, value);
                // the files are read again on a language change, so an edited file shows without a restart, and its problems are logged again
                reported.Clear();
                Reload();
            }
        }

        /// <summary>Forgets the loaded files and the chosen language; the next lookup reads them again.</summary>
        public static void Reload()
        {
            tables.Clear();
            folder = null;
            current = null;
        }

        public static string Get(string key) => Find(Current, key) ?? Find("en", key) ?? Miss(key);

        /// <summary>The key's tooltip, else the English one, else none.</summary>
        public static string Tip(string key) => Find(Current, key + ".tip") ?? Find("en", key + ".tip") ?? "";

        public static GUIContent Content(string key) => new GUIContent(Get(key), Tip(key));

        /// <summary>The text with its arguments, numbers in the invariant culture; a text that does not fit them falls back like a missing one.</summary>
        public static string Format(string key, params object[] args)
        {
            args ??= Array.Empty<object>();
            var text = Find(Current, key);
            if (text != null)
            {
                if (TryFormat(text, args, out var result)) return result;
                Report($"'{key}' in {Current} does not fit its {args.Length} arguments");
            }
            var english = Find("en", key);
            return english != null && TryFormat(english, args, out var fallback) ? fallback : Miss(key);
        }

        public static string Text(Message message) => Format(message.Key, message.Args);

        internal static bool Has(string code, string key) => Find(code, key) != null;

        private static readonly Regex Placeholder = new Regex(@"\{(\d{1,9})(?:[,:][^}]*)?\}");

        /// <summary>The text uses exactly the arguments 0..n−1 and formats without an exception; a text without braces needs no arguments.</summary>
        private static bool TryFormat(string text, object[] args, out string result)
        {
            result = null;
            if (args.Length == 0 && text.IndexOf('{') < 0)
            {
                result = text;
                return true;
            }
            var used = new HashSet<int>(Placeholder.Matches(text).Cast<Match>().Select(m => int.Parse(m.Groups[1].Value)));
            if (used.Count != args.Length || used.Any(i => i >= args.Length)) return false;
            try
            {
                result = string.Format(CultureInfo.InvariantCulture, text, args);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static string Miss(string key)
        {
            Missing?.Invoke(key);
            return key;
        }

        /// <summary>The language's text of the key; an empty one counts as missing.</summary>
        private static string Find(string code, string key) => Table(code).TryGetValue(key, out var text) && !string.IsNullOrEmpty(text) ? text : null;

        /// <summary>A language's strings as loaded (empty when the file is missing or malformed).</summary>
        internal static Dictionary<string, string> Table(string code)
        {
            if (tables.TryGetValue(code, out var table)) return table;
            table = new Dictionary<string, string>();
            var path = Path.Combine(Folder, code + ".json");
            try
            {
                if (File.Exists(path)) table = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path)) ?? table;
            }
            catch (Exception e)
            {
                Report($"{path} could not be read, its strings fall back to English: {e.Message}");
            }
            return tables[code] = table;
        }

        /// <summary>Next to the editor assembly definition.</summary>
        internal static string Folder
        {
            get
            {
                if (FolderOverride != null) return FolderOverride;
                if (folder != null) return folder;
                var asmdef = AssetDatabase.FindAssets($"{Asmdef} t:AssemblyDefinitionAsset").Select(AssetDatabase.GUIDToAssetPath)
                    .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p) == Asmdef);
                if (asmdef == null) Report($"language files not found: no {Asmdef} asmdef");
                return folder = asmdef == null ? "" : Path.GetDirectoryName(asmdef).Replace('\\', '/') + "/Localization";
            }
        }

        private static void Report(string problem)
        {
            if (reported.Add(problem)) Debug.LogWarning("[Blendshape Transfer] " + problem);
        }
    }
}
