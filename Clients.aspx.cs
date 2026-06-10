using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Web.Configuration;
using System.Web.UI;
using System.Web.UI.WebControls;

public partial class Clients : Page
{
    private Dictionary<int, SyncLog> _lastRuns;

    protected void Page_Load(object sender, EventArgs e)
    {
        var action = Request.QueryString["action"];

        if (!IsPostBack)
        {
            if (action == "new" || action == "edit")
            {
                pnlForm.Visible = true;
                pnlList.Visible = false;

                if (action == "edit")
                {
                    int id;
                    if (int.TryParse(Request.QueryString["id"], out id))
                    {
                        var client = DB.GetClient(id);
                        if (client != null)
                        {
                            hfClientId.Value  = id.ToString();
                            txtName.Text      = client.ClientName;
                            txtPath.Text      = client.AccessFolderPath;
                            txtDb.Text        = client.TargetDatabase;
                            chkActive.Checked = client.IsActive;
                            lblFormTitle.Text = "עריכת לקוח";
                        }
                    }
                }
            }
            else
            {
                pnlList.Visible = true;
                pnlForm.Visible = false;
                BindClients();
            }
        }
        else
        {
            _lastRuns = DB.GetLastRunPerClient();
        }
    }

    private void BindClients()
    {
        _lastRuns = DB.GetLastRunPerClient();
        var clients = DB.GetClients();
        rptClients.DataSource = clients;
        rptClients.DataBind();
        phEmpty.Visible = clients.Count == 0;
        litNextRun.Text = GetNextScheduledRun();
    }

    private static string GetNextScheduledRun()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks",
                "/query /tn \"Task_Today\" /fo LIST")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true
            };
            using (var p = Process.Start(psi))
            {
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                foreach (var line in output.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Next Run Time:", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("זמן הריצה הבא:", StringComparison.OrdinalIgnoreCase))
                    {
                        var val = trimmed.Substring(trimmed.IndexOf(':') + 1).Trim();
                        DateTime dt;
                        if (DateTime.TryParse(val, out dt))
                            return dt.ToString("HH:mm:ss");
                        return val;
                    }
                }
            }
        }
        catch { }
        return "—";
    }

    private static void TriggerTask(string taskName)
    {
        try
        {
            var proc = Process.Start(new ProcessStartInfo("schtasks", "/run /tn \"" + taskName + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow  = true
            });
            if (proc != null) proc.WaitForExit(1000);
        }
        catch { }
    }

    protected void btnSave_Click(object sender, EventArgs e)
    {
        if (!Page.IsValid) return;

        int clientId;
        int.TryParse(hfClientId.Value, out clientId);

        var name   = txtName.Text.Trim();
        var path   = txtPath.Text.Trim();
        var db     = txtDb.Text.Trim();
        var active = chkActive.Checked;

        if (clientId == 0)
        {
            DB.AddClient(name, path, db, active);
            Session["Success"] = "הלקוח נוסף בהצלחה.";
        }
        else
        {
            DB.UpdateClient(clientId, name, path, db, active);
            Session["Success"] = "הלקוח עודכן בהצלחה.";
        }

        Response.Redirect("Clients.aspx");
    }

    protected void rptClients_ItemCommand(object source, RepeaterCommandEventArgs e)
    {
        int id;
        int.TryParse(e.CommandArgument.ToString(), out id);

        switch (e.CommandName)
        {
            case "Delete":
                DB.DeleteClient(id);
                Session["Success"] = "הלקוח נמחק.";
                break;

            case "Toggle":
                DB.ToggleActive(id);
                break;

            case "RunFull":
                DB.ClearClientMtime(id);
                DB.SetRunRequested(id);
                TriggerTask("Task_90Days");
                TriggerTask("Task_Today");
                Session["Success"] = "סנכרון מלא הופעל — יסרוק כל ההיסטוריה מהיום הראשון.";
                break;

            case "RunToday":
                DB.SetRunRequested(id);
                TriggerTask("Task_Today");
                Session["Success"] = "סנכרון היום הופעל.";
                break;

            case "Run90Days":
                DB.SetRunRequested(id);
                TriggerTask("Task_90Days");
                Session["Success"] = "סנכרון 90 יום הופעל.";
                break;
        }

        Response.Redirect("Clients.aspx");
    }

    // ── Helper methods עבור ה-Repeater ──────────────────────────────────────

    protected string GetLastRunInfo(int clientId)
    {
        if (_lastRuns == null) _lastRuns = DB.GetLastRunPerClient();
        SyncLog log;
        if (!_lastRuns.TryGetValue(clientId, out log) || log == null)
            return "<span class=\"text-muted\">—</span>";

        var time   = log.RunStartedAt.ToLocalTime().ToString("dd/MM HH:mm");
        string badge, text;
        if (log.Status == "Success")      { badge = "bg-success";              text = "תקין";  }
        else if (log.Status == "Error")   { badge = "bg-danger";               text = "שגיאה"; }
        else                              { badge = "bg-warning text-dark";    text = "דולג";  }

        return string.Format("{0} <span class=\"badge {1}\">{2}</span>", time, badge, text);
    }

    protected string GetLastInserts(int clientId)
    {
        if (_lastRuns == null) _lastRuns = DB.GetLastRunPerClient();
        SyncLog log;
        if (!_lastRuns.TryGetValue(clientId, out log) || log == null || log.InsertCount == 0)
            return "<span class=\"text-muted\">—</span>";
        return "<span class=\"text-success\">+" + log.InsertCount + "</span>";
    }

    protected string GetLastUpdates(int clientId)
    {
        if (_lastRuns == null) _lastRuns = DB.GetLastRunPerClient();
        SyncLog log;
        if (!_lastRuns.TryGetValue(clientId, out log) || log == null || log.UpdateCount == 0)
            return "<span class=\"text-muted\">—</span>";
        return "<span class=\"text-primary\">~" + log.UpdateCount + "</span>";
    }
}
