using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
using L = Akiya82.BlendshapeTransfer.Localization;

namespace Akiya82.BlendshapeTransfer
{
    internal sealed partial class ShapeTransferWindow
    {
        partial void DrawResultSection()
        {
            if (session == null) return;
            var upToDate = fresh.Count(f => f);
            var warned = session.Garments.Count(g => g.WarningCount > 0);
            if (!Section("result", Title("section.result", upToDate, session.Garments.Count, warned))) return;
            for (var k = 0; k < session.Garments.Count; k++) DrawResultRow(k);
            if (GUILayout.Button(L.Content("result.copy")))
            {
                EditorGUIUtility.systemCopyBuffer = ReportText.Of(session);
                ShowNotification(new GUIContent(L.Get("result.copied")));
            }
        }

        private void DrawResultRow(int k)
        {
            var g = session.Garments[k];
            var upToDate = k < fresh.Length && fresh[k];
            var icon = EditorGUIUtility.IconContent(upToDate ? "TestPassed" : "d_Refresh").image;
            var title = new GUIContent(ReportText.Summary(g.Renderer.name, g.Report, g.WarningCount, upToDate), icon, L.Get(upToDate ? "garments.upToDate" : "garments.stale"));
            if (!Fold("Result." + g.Data.rendererPath, title, openByDefault: false)) return;
            EditorGUI.indentLevel++;
            if (g.Report != null)
                foreach (var line in ReportText.Lines(g.Report)) EditorGUILayout.LabelField(new GUIContent(line.Text, L.Tip(line.Key)), EditorStyles.wordWrappedLabel);
            foreach (var w in g.Warnings) EditorGUILayout.HelpBox(L.Text(w), MessageType.Warning);
            foreach (var m in session.BaseWeightInfo(g)) EditorGUILayout.HelpBox(L.Text(m), MessageType.Info);
            using (new EditorGUI.DisabledScope(!session.CanRestore(g)))
            {
                if (GUILayout.Button(L.Content("result.restore")))
                {
                    Restore(g);
                    GUIUtility.ExitGUI();
                }
            }
            EditorGUI.indentLevel--;
        }

        private void Restore(GarmentState g)
        {
            try
            {
                session.Restore(g);
                statusOverride = null;
                savedAt = null;
                ShowNotification(new GUIContent(L.Format("status.restored", g.Renderer.name)));
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                statusOverride = L.Format("status.restoreFailed", e.Message);
            }
        }
    }
}
