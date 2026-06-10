using System;
using System.Web.UI;

public partial class Logs : Page
{
    private int? _selectedClientId;

    protected void Page_Load(object sender, EventArgs e)
    {
        int id;
        _selectedClientId = int.TryParse(Request.QueryString["clientId"], out id) ? id : (int?)null;

        var clients = DB.GetClients();
        rptClientFilter.DataSource = clients;
        rptClientFilter.DataBind();

        var logs = DB.GetLogs(_selectedClientId);
        rptLogs.DataSource = logs;
        rptLogs.DataBind();
        phEmpty.Visible = logs.Count == 0;
    }

    protected string IsSelected(int clientId)
    {
        return _selectedClientId.HasValue && _selectedClientId.Value == clientId
            ? "selected=\"selected\""
            : "";
    }

    protected string GetBadge(string status)
    {
        if (status == "Success") return "<span class=\"badge bg-success\">תקין</span>";
        if (status == "Error")   return "<span class=\"badge bg-danger\">שגיאה</span>";
        return "<span class=\"badge bg-warning text-dark\">דולג</span>";
    }

    protected string GetDuration(object start, object finish)
    {
        if (finish == null || finish is DBNull) return "";
        var diff = (DateTime)finish - (DateTime)start;
        return (int)diff.TotalSeconds + "s";
    }
}
