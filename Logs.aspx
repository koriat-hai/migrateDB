<%@ Page Language="C#" AutoEventWireup="true" CodeFile="Logs.aspx.cs"
         Inherits="Logs" MasterPageFile="~/Site.master" %>

<asp:Content ID="Head" ContentPlaceHolderID="HeadContent" runat="server" />

<%-- ── Hero ──────────────────────────────────────────────────────────── --%>
<asp:Content ID="Hero" ContentPlaceHolderID="HeroContent" runat="server">
    <div class="ss-hero">
        <div class="ss-hero-inner">
            <div>
                <h1>&#128196; לוגי סנכרון</h1>
                <p>SmartScale Worker &middot; 48 שעות אחרונות</p>
            </div>
        </div>
    </div>
</asp:Content>

<asp:Content ID="Main" ContentPlaceHolderID="MainContent" runat="server">

    <div class="ss-bar">
        <span class="ss-bar-title">יומן ריצות</span>
        <div class="d-flex align-items-center gap-2 flex-wrap">
            <label class="ss-bar-meta mb-0">סנן לפי לקוח:</label>
            <select id="clientFilter" class="form-select form-select-sm" style="min-width:160px"
                    onchange="window.location='Logs.aspx'+(this.value?'?clientId='+this.value:'')">
                <option value="">כל הלקוחות</option>
                <asp:Repeater ID="rptClientFilter" runat="server">
                    <ItemTemplate>
                        <option value="<%# Eval("ClientId") %>"
                                <%# IsSelected((int)Eval("ClientId")) %>>
                            <%# Eval("ClientName") %>
                        </option>
                    </ItemTemplate>
                </asp:Repeater>
            </select>
            <% if (!string.IsNullOrEmpty(Request.QueryString["clientId"])) { %>
            <a href="Logs.aspx" class="btn btn-sm btn-outline-secondary">&#x00D7; נקה</a>
            <% } %>
        </div>
    </div>

    <div class="card">
        <div class="card-body p-0">
            <table class="table table-hover align-middle mb-0">
                <thead class="table-dark">
                    <tr>
                        <th>לקוח</th>
                        <th>טבלה</th>
                        <th>סטטוס</th>
                        <th class="text-center">הכנסות</th>
                        <th class="text-center">עדכונים</th>
                        <th>התחלה</th>
                        <th>משך</th>
                        <th>הודעה</th>
                    </tr>
                </thead>
                <tbody>
                    <asp:Repeater ID="rptLogs" runat="server">
                        <ItemTemplate>
                            <tr>
                                <td class="fw-semibold"><%# Eval("ClientName") %></td>
                                <td><code><%# Eval("TableName") %></code></td>
                                <td><%# GetBadge((string)Eval("Status")) %></td>
                                <td class="text-center small">
                                    <%# (int)Eval("InsertCount") > 0
                                        ? "<span class=\"text-success fw-semibold\">+" + Eval("InsertCount") + "</span>"
                                        : "<span class=\"text-muted\">—</span>" %>
                                </td>
                                <td class="text-center small">
                                    <%# (int)Eval("UpdateCount") > 0
                                        ? "<span class=\"text-primary fw-semibold\">~" + Eval("UpdateCount") + "</span>"
                                        : "<span class=\"text-muted\">—</span>" %>
                                </td>
                                <td class="small text-nowrap">
                                    <%# ((DateTime)Eval("RunStartedAt")).ToLocalTime().ToString("dd/MM HH:mm:ss") %>
                                </td>
                                <td class="small"><%# GetDuration(Eval("RunStartedAt"), Eval("RunFinishedAt")) %></td>
                                <td class="small text-muted"
                                    style="max-width:260px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap"
                                    title="<%# Eval("Message") %>">
                                    <%# Eval("Message") %>
                                </td>
                            </tr>
                        </ItemTemplate>
                    </asp:Repeater>
                    <asp:PlaceHolder ID="phEmpty" runat="server" Visible="false">
                        <tr>
                            <td colspan="8" class="empty-state">אין לוגים עדיין.</td>
                        </tr>
                    </asp:PlaceHolder>
                </tbody>
            </table>
        </div>
    </div>

</asp:Content>
