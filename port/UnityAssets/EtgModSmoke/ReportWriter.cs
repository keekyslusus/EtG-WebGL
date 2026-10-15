using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// report.json (JsonUtility) and report.md of the mod smoke test. Same C# 4 restrictions as the editor scripts.
namespace EtgModSmoke
{
    [Serializable]
    public class Report
    {
        public string started;
        public float minutes;
        public string state;
        public string phases;
        public string filter;
        public string mods;
        public string expandStatus;
        public int attempt = 1;
        public List<string> runNotes = new List<string>();
        public List<string> filteredNoise = new List<string>();
        public List<ErrorGroup> errors = new List<ErrorGroup>();
        public List<StepResult> steps = new List<StepResult>();
    }

    public static class ReportWriter
    {
        private static readonly string[] Categories = { Category.Port, Category.Mod, Category.Other, Category.PlayMode, Category.Smoke };

        private static string Title(string category)
        {
            switch (category)
            {
                case Category.Port: return "Port errors (fix in the port: ModBaker / AccessFixer / facades / shims)";
                case Category.Mod: return "Probably mod bugs (thrown in a mod's own code)";
                case Category.PlayMode: return "Play Mode limitations (editor only, not in the player)";
                case Category.Smoke: return "Smoke test's own errors";
                default: return "Game code / unclear";
            }
        }

        public static string Markdown(Report r)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Mod smoke test");
            sb.AppendLine();
            sb.AppendLine("- attempt " + r.attempt + (r.attempt > 1 ? " (the editor was restarted before: steps of earlier attempts are carried over)" : ""));
            sb.AppendLine("- started " + r.started + ", " + r.minutes.ToString("0.0", CultureInfo.InvariantCulture) + " min, state: **" + r.state + "**");
            sb.AppendLine("- phases: " + r.phases + (string.IsNullOrEmpty(r.filter) ? "" : "; **filter: " + r.filter + "**"));
            sb.AppendLine("- mods: " + r.mods + "; Expand the Gungeon load status: " + (r.expandStatus ?? "-"));
            foreach (string n in r.runNotes)
            {
                sb.AppendLine("- " + n);
            }
            sb.AppendLine();

            sb.AppendLine("## Summary");
            sb.AppendLine();
            sb.AppendLine("| kind | steps | ok | errors | failed | timeout | hang | skipped | pending |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
            List<string> kinds = new List<string>();
            foreach (StepResult s in r.steps)
            {
                if (!kinds.Contains(s.kind))
                {
                    kinds.Add(s.kind);
                }
            }
            foreach (string k in kinds)
            {
                int total = 0, ok = 0, err = 0, failed = 0, timeout = 0, hang = 0, skipped = 0, pending = 0;
                foreach (StepResult s in r.steps)
                {
                    if (s.kind != k)
                    {
                        continue;
                    }
                    total++;
                    switch (s.status)
                    {
                        case "ok": ok++; break;
                        case "errors": err++; break;
                        case "failed": failed++; break;
                        case "timeout": timeout++; break;
                        case "hang": hang++; break;
                        case "skipped": skipped++; break;
                        default: pending++; break;
                    }
                }
                sb.AppendLine("| " + k + " | " + total + " | " + ok + " | " + err + " | " + failed + " | " + timeout + " | " + hang + " | " + skipped + " | " + pending + " |");
            }
            sb.AppendLine();
            sb.AppendLine("Error kinds (distinct signatures) by category:");
            sb.AppendLine();
            foreach (string c in Categories)
            {
                int kindsN = 0, count = 0;
                foreach (ErrorGroup g in r.errors)
                {
                    if (g.category == c)
                    {
                        kindsN++;
                        count += g.count;
                    }
                }
                if (kindsN > 0)
                {
                    sb.AppendLine("- **" + c + "**: " + kindsN + " kinds, " + count + " messages - " + Title(c));
                }
            }
            if (r.filteredNoise.Count > 0)
            {
                sb.AppendLine("- filtered known noise of the game: " + string.Join("; ", r.filteredNoise.ToArray()));
            }
            sb.AppendLine();

            List<ErrorGroup> recurring = r.errors.FindAll(delegate(ErrorGroup g) { return g.frames >= 100; });
            if (recurring.Count > 0)
            {
                recurring.Sort(delegate(ErrorGroup a, ErrorGroup b) { return b.frames.CompareTo(a.frames); });
                sb.AppendLine("## Recurring (logged in 100+ frames: an error every frame)");
                sb.AppendLine();
                foreach (ErrorGroup g in recurring)
                {
                    sb.AppendLine("- [" + g.category + "] **" + Esc(g.type) + "** " + Esc(g.message) + " - " + g.frames + " frames, first in `" + g.firstStep + "`");
                }
                sb.AppendLine();
            }

            foreach (string c in Categories)
            {
                List<ErrorGroup> groups = r.errors.FindAll(delegate(ErrorGroup g) { return g.category == c; });
                if (groups.Count == 0)
                {
                    continue;
                }
                groups.Sort(delegate(ErrorGroup a, ErrorGroup b)
                {
                    int x = b.stepCount.CompareTo(a.stepCount);
                    return x != 0 ? x : b.count.CompareTo(a.count);
                });
                sb.AppendLine("## " + Title(c));
                sb.AppendLine();
                foreach (ErrorGroup g in groups)
                {
                    AppendGroup(sb, g, true);
                }
            }

            sb.AppendLine("## Steps");
            sb.AppendLine();
            sb.AppendLine("| # | kind | id | name | owner | type | status | port | mod | other | playmode | s | notes |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
            for (int i = 0; i < r.steps.Count; i++)
            {
                StepResult s = r.steps[i];
                string status = s.status == "ok" || s.status == "pending" ? s.status : "**" + s.status + "**";
                sb.AppendLine("| " + (i + 1) + " | " + s.kind + " | " + Esc(s.id) + " | " + Esc(s.name) + " | " + Esc(s.owner) + " | " + Esc(s.detail) +
                              " | " + status + " | " + N(s.Count(Category.Port)) + " | " + N(s.Count(Category.Mod)) + " | " + N(s.Count(Category.Other)) +
                              " | " + N(s.Count(Category.PlayMode)) + " | " + s.seconds.ToString("0.0", CultureInfo.InvariantCulture) + " | " + Esc(string.Join("; ", s.notes.ToArray())) + " |");
            }
            sb.AppendLine();

            sb.AppendLine("## Step details (errors, failures, timeouts)");
            sb.AppendLine();
            foreach (StepResult s in r.steps)
            {
                if (s.errors.Count == 0 && (s.status == "ok" || s.status == "pending" || s.status == "skipped"))
                {
                    continue;
                }
                sb.AppendLine("### " + s.kind + " `" + s.id + "` " + Esc(s.name) + " - " + s.status);
                sb.AppendLine();
                sb.AppendLine("owner " + (s.owner ?? "-") + ", " + s.seconds.ToString("0.0", CultureInfo.InvariantCulture) + " s, " + s.frames + " frames, errors in " + s.errorFrames + " of them" +
                              (s.frames > 30 && s.errorFrames * 2 > s.frames ? " (**every frame**)" : "") + ". " + string.Join("; ", s.notes.ToArray()));
                sb.AppendLine();
                foreach (ErrorGroup g in s.errors)
                {
                    AppendGroup(sb, g, false);
                }
            }
            return sb.ToString();
        }

        private static void AppendGroup(StringBuilder sb, ErrorGroup g, bool global)
        {
            sb.Append("- [" + g.category + "] **" + Esc(g.type) + "** " + Esc(g.message) + " - " + g.count + "x in " + g.frames + " frames (max " + g.maxPerFrame + "/frame)");
            if (global)
            {
                sb.Append(", " + g.stepCount + " steps: " + string.Join(", ", g.steps.ToArray()) + (g.stepCount > g.steps.Count ? ", ..." : ""));
            }
            sb.AppendLine();
            if (!string.IsNullOrEmpty(g.stack))
            {
                sb.AppendLine("  ```");
                foreach (string line in g.stack.Split('\n'))
                {
                    sb.AppendLine("  " + line);
                }
                sb.AppendLine("  ```");
            }
        }

        private static string N(int n)
        {
            return n == 0 ? "" : n.ToString();
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }
            return s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }
    }
}
