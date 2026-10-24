using System.Collections.Generic;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>A run of body shapes under one separator; the group before the first separator has no title.</summary>
    internal sealed class ShapeGroup
    {
        public string Title;
        /// <summary>How many groups before this one have the same title.</summary>
        public int Occurrence;
        public readonly List<int> Shapes = new List<int>();

        /// <summary>The key the group's fold state is remembered under.</summary>
        public string Key => $"{Title}#{Occurrence}";
    }

    /// <summary>
    /// Separator shapes, the empty shapes an author puts between groups ("===== Nail ====="): the name, trimmed, starts with a
    /// character c out of = - # ~ *, starts and ends with three or more c, and keeps text after trimming every c and then
    /// whitespace from both ends. The rule stays narrow: a missed separator stays an ordinary row; a false one
    /// would make a real shape untransferable.
    /// </summary>
    internal static class ShapeGroups
    {
        private const string Marks = "=-#~*";

        /// <summary>The separator's title, or null for an ordinary shape.</summary>
        public static string SeparatorTitle(string name)
        {
            var n = name.Trim();
            if (n.Length == 0 || Marks.IndexOf(n[0]) < 0) return null;
            var c = n[0];
            if (n.Length - n.TrimStart(c).Length < 3 || n.Length - n.TrimEnd(c).Length < 3) return null;
            var title = n.Trim(c).Trim();
            return title.Length > 0 ? title : null;
        }

        public static bool IsSeparator(string name) => SeparatorTitle(name) != null;

        /// <summary>The shapes in order, a new group at each separator; the separators themselves are in no group.</summary>
        public static List<ShapeGroup> Of(IReadOnlyList<string> names)
        {
            var groups = new List<ShapeGroup> { new ShapeGroup() };
            var seen = new Dictionary<string, int>();
            for (var i = 0; i < names.Count; i++)
            {
                var title = SeparatorTitle(names[i]);
                if (title == null)
                {
                    groups[groups.Count - 1].Shapes.Add(i);
                    continue;
                }
                seen.TryGetValue(title, out var occurrence);
                seen[title] = occurrence + 1;
                groups.Add(new ShapeGroup { Title = title, Occurrence = occurrence });
            }
            if (groups[0].Shapes.Count == 0) groups.RemoveAt(0);
            return groups;
        }
    }
}
