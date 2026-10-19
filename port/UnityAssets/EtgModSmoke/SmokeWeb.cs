using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

// The smoke test in the WebGL player (port/README.md, Mods -> Smoke test): started by the page's URL,
// ?smoketest[=phases][&only=a,b][&resume]. The report goes to persistentDataPath/modsmoke (IndexedDB, synced after
// every write) and to the page (EtgModSmoke.jslib -> window.EtgSmoke; port/site saves it when the run is over).
// A level load that broke ends the attempt with "restart before X", and the page reloads itself with &resume, like
// port/modsmoke.sh restarts the editor; after a hang (the page freezes) reload it by hand with &resume.
namespace EtgModSmoke
{
    public partial class SmokeTest
    {
        [DllImport("__Internal")] private static extern void EtgSmokeSync();
        [DllImport("__Internal")] private static extern void EtgSmokeReport(string state, string markdown, string json, int finished);

        private static bool s_webResume;

        public static string WebReportDir
        {
            get { return Path.Combine(Application.persistentDataPath, "modsmoke"); }
        }

        private static void WebAutoStart()
        {
            string query = Application.absoluteURL;
            int q = query.IndexOf('?');
            if (q < 0)
            {
                return;
            }
            query = query.Substring(q + 1);
            int hash = query.IndexOf('#');
            if (hash >= 0)
            {
                query = query.Substring(0, hash);
            }
            string phases = null, only = "";
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                string key = Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq));
                string value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                if (key == "smoketest")
                {
                    phases = value.Length > 0 ? value : "all";
                }
                else if (key == "only")
                {
                    only = value;
                }
                else if (key == "resume")
                {
                    s_webResume = true;
                }
            }
            if (phases == null)
            {
                return;
            }
            // non-null: Finish("restart before X") is allowed; the page does the restart (reload with &resume)
            Resumable = true;
            Finished = delegate { };
            Debug.Log(SmokeLog.Prefix + "WebGL: phases " + phases + (only.Length > 0 ? ", only " + only : "") +
                      (s_webResume ? ", resuming" : "") + "; report in " + WebReportDir);
            Begin(phases, only, WebReportDir);
        }

        private string WebWriteReport(Report r)
        {
            Directory.CreateDirectory(m_reportDir);
            string json = JsonUtility.ToJson(r, true);
            string markdown = ReportWriter.Markdown(r);
            File.WriteAllText(Path.Combine(m_reportDir, "report.json"), json);
            string md = Path.Combine(m_reportDir, "report.md");
            File.WriteAllText(md, markdown);
            EtgSmokeSync();
            EtgSmokeReport(m_state, markdown, json, m_finished ? 1 : 0);
            return md;
        }
    }
}
