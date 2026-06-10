using System;
using System.Web.UI;

public partial class SiteMaster : MasterPage
{
    protected void Page_Load(object sender, EventArgs e) { }

    protected string IsActive(string page)
    {
        var path = Request.Url.AbsolutePath;
        return path.IndexOf(page, StringComparison.OrdinalIgnoreCase) >= 0 ? "active" : "";
    }
}
