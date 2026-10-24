using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Akiya82.BlendshapeTransfer
{
    /// <summary>One source of the report as text in the current language: the result section draws these rows and Copy report joins them.</summary>
    internal static class ReportText
    {
        public static string Of(TransferSession session)
        {
            var fingerprints = session.Fingerprints();
            return string.Join("\n", session.Garments.Select((g, k) =>
                Of(g.Renderer.name, g.Report, g.Warnings, session.BaseWeightInfo(g), g.WarningCount, session.IsUpToDate(g, fingerprints[k]))));
        }

        public static string Of(string garment, GarmentReport report, IEnumerable<Message> warnings, IEnumerable<Message> info, int warningCount, bool upToDate)
        {
            var text = new StringBuilder(Summary(garment, report, warningCount, upToDate)).AppendLine();
            void Line(string s) => text.Append("  ").AppendLine(s);
            if (report != null)
                foreach (var line in Lines(report)) Line(line.Text);
            foreach (var w in warnings.Concat(info)) Line(Localization.Text(w));
            return text.ToString();
        }

        /// <summary>The garment's one-line result; a report older than the garment's inputs says it is the last transfer.</summary>
        public static string Summary(string garment, GarmentReport report, int warnings, bool upToDate) =>
            report == null
                ? Localization.Format("report.summaryNone", garment)
                : Localization.Format("report.summary", garment, report.Written.Count, report.MaxMove * 1000f, warnings)
                  + (upToDate ? "" : " " + Localization.Get("report.lastTransfer"));

        /// <summary>The report's rows: the key (its tooltip is Key + ".tip") and the formatted text.</summary>
        internal static IEnumerable<(string Key, string Text)> Lines(GarmentReport report)
        {
            yield return ("report.written", Localization.Format("report.written", report.Written.Count > 0 ? string.Join(", ", report.Written) : Localization.Get("report.none")));
            if (report.Skipped.Count > 0)
                yield return ("report.skipped", Localization.Format("report.skipped", string.Join("; ", report.Skipped.Select(s => Localization.Format("report.skippedEntry", s.name, Localization.Get(s.reason))))));
            if (report.BaseOffset.Count > 0)
                yield return ("report.baseOffset", Localization.Format("report.baseOffset", string.Join(", ", report.BaseOffset.Select(b => Localization.Format("report.baseOffsetEntry", b.name, b.weight)))));
            yield return ("report.fallback", Localization.Format("report.fallback", report.Fallback));
            yield return ("report.singular", Localization.Format("report.singular", report.Singular));
            yield return ("report.weightMismatch", Localization.Format("report.weightMismatch", report.WeightMismatch));
            yield return ("report.pushed", Localization.Format("report.pushed", report.Clip.Pushed, report.Clip.MaxPush * 1000f));
            yield return ("report.unresolved", Localization.Format("report.unresolved", report.Clip.Unresolved));
            yield return ("report.stillSkin", Localization.Format("report.stillSkin", report.Clip.StillSkin));
            yield return ("report.stillLayer", Localization.Format("report.stillLayer", report.Clip.StillLayer));
            yield return ("report.intermediate", Localization.Format("report.intermediate", report.Clip.Intermediate));
        }
    }
}
