<%@ Page Language="C#" AutoEventWireup="true" CodeFile="Clients.aspx.cs"
         Inherits="Clients" MasterPageFile="~/Site.master" %>

<asp:Content ID="Head" ContentPlaceHolderID="HeadContent" runat="server" />

<%-- ── Hero ──────────────────────────────────────────────────────────── --%>
<asp:Content ID="Hero" ContentPlaceHolderID="HeroContent" runat="server">
    <div class="ss-hero">
        <div class="ss-hero-inner">
            <div>
                <h1>&#128101; ניהול לקוחות</h1>
                <p>SmartScale Worker &middot; לקוחות פעילים וסנכרון אוטומטי</p>
            </div>
        </div>
    </div>
</asp:Content>

<asp:Content ID="Main" ContentPlaceHolderID="MainContent" runat="server">

    <%-- ── טופס הוספה / עריכה ──────────────────────────────────────────────── --%>
    <asp:Panel ID="pnlForm" runat="server" Visible="false">
        <div class="card mb-4" style="max-width:560px">
            <div class="card-header">
                <asp:Label ID="lblFormTitle" runat="server" Text="הוספת לקוח" />
            </div>
            <div class="card-body">
                <asp:HiddenField ID="hfClientId" runat="server" Value="0" />

                <div class="mb-3">
                    <label class="form-label">שם לקוח</label>
                    <asp:TextBox ID="txtName" runat="server" CssClass="form-control" MaxLength="200" />
                    <asp:RequiredFieldValidator runat="server" ControlToValidate="txtName"
                        ErrorMessage="שדה חובה" CssClass="text-danger small" Display="Dynamic" />
                </div>

                <div class="mb-3">
                    <label class="form-label">נתיב תיקיית Access</label>
                    <asp:TextBox ID="txtPath" runat="server" CssClass="form-control" dir="ltr" MaxLength="500" />
                    <asp:RequiredFieldValidator runat="server" ControlToValidate="txtPath"
                        ErrorMessage="שדה חובה" CssClass="text-danger small" Display="Dynamic" />
                    <div class="form-text text-muted">לדוגמה: C:\Synology\ClientA</div>
                </div>

                <div class="mb-3">
                    <label class="form-label">שם DB יעד ב-SQL Server</label>
                    <asp:TextBox ID="txtDb" runat="server" CssClass="form-control" dir="ltr" MaxLength="128" />
                    <asp:RequiredFieldValidator runat="server" ControlToValidate="txtDb"
                        ErrorMessage="שדה חובה" CssClass="text-danger small" Display="Dynamic" />
                    <asp:RegularExpressionValidator runat="server" ControlToValidate="txtDb"
                        ValidationExpression="^[a-zA-Z0-9_\-]+$"
                        ErrorMessage="רק אותיות אנגלית, ספרות, קו תחתון או מקף"
                        CssClass="text-danger small" Display="Dynamic" />
                </div>

                <div class="mb-4 form-check">
                    <asp:CheckBox ID="chkActive" runat="server" CssClass="form-check-input" Checked="true" />
                    <label class="form-check-label">פעיל</label>
                </div>

                <div class="d-flex gap-2">
                    <asp:Button ID="btnSave" runat="server" Text="שמור"
                        CssClass="btn btn-primary" OnClick="btnSave_Click" />
                    <a href="Clients.aspx" class="btn btn-outline-secondary">ביטול</a>
                </div>
            </div>
        </div>
    </asp:Panel>

    <%-- ── רשימת לקוחות ────────────────────────────────────────────────────── --%>
    <asp:Panel ID="pnlList" runat="server">
        <div class="ss-bar">
            <span class="ss-bar-title">רשימת לקוחות</span>
            <div class="d-flex align-items-center gap-3 flex-wrap">
                <span class="ss-bar-meta">
                    &#128337; ריצה מתוזמנת הבאה:
                    <strong><asp:Literal ID="litNextRun" runat="server" Text="—" /></strong>
                </span>
                <a href="Clients.aspx?action=new" class="btn btn-primary btn-sm">+ הוסף לקוח</a>
            </div>
        </div>

        <div class="card">
            <div class="card-body p-0">
                <table class="table table-hover align-middle mb-0">
                    <thead class="table-dark">
                        <tr>
                            <th>שם לקוח</th>
                            <th>DB יעד</th>
                            <th>סטטוס</th>
                            <th>ריצה אחרונה</th>
                            <th class="text-center">+הכנסות</th>
                            <th class="text-center">~עדכונים</th>
                            <th>פעולות</th>
                        </tr>
                    </thead>
                    <tbody>
                        <asp:Repeater ID="rptClients" runat="server" OnItemCommand="rptClients_ItemCommand">
                            <ItemTemplate>
                                <tr>
                                    <td class="fw-semibold"><%# Eval("ClientName") %></td>
                                    <td dir="ltr"><code><%# Eval("TargetDatabase") %></code></td>
                                    <td>
                                        <%# (bool)Eval("IsActive")
                                            ? "<span class=\"badge bg-success\">פעיל</span>"
                                            : "<span class=\"badge bg-secondary\">מושבת</span>" %>
                                    </td>
                                    <td class="small"><%# GetLastRunInfo((int)Eval("ClientId")) %></td>
                                    <td class="text-center small"><%# GetLastInserts((int)Eval("ClientId")) %></td>
                                    <td class="text-center small"><%# GetLastUpdates((int)Eval("ClientId")) %></td>
                                    <td>
                                        <div class="d-flex flex-column gap-2">
                                            <div class="d-flex gap-2">
                                                <asp:Button runat="server" Text="&#8635; סנכרן הכל"
                                                    CommandName="RunFull"
                                                    CommandArgument='<%# Eval("ClientId") %>'
                                                    CssClass="btn btn-sm btn-success"
                                                    CausesValidation="false"
                                                    ToolTip="סריקה מלאה — כל ההיסטוריה מהיום הראשון"
                                                    OnClientClick="return confirm('סנכרן הכל — יסרוק את כל ההיסטוריה מהיום הראשון.\nהאם אתה בטוח?')" />
                                                <asp:Button runat="server" Text="&#8635; היום"
                                                    CommandName="RunToday"
                                                    CommandArgument='<%# Eval("ClientId") %>'
                                                    CssClass="btn btn-sm btn-outline-success"
                                                    CausesValidation="false"
                                                    ToolTip="סנכרן את נתוני היום בלבד" />
                                                <asp:Button runat="server" Text="&#8635; 90 יום"
                                                    CommandName="Run90Days"
                                                    CommandArgument='<%# Eval("ClientId") %>'
                                                    CssClass="btn btn-sm btn-outline-secondary"
                                                    CausesValidation="false"
                                                    ToolTip="סנכרן 90 ימים אחרונים" />
                                            </div>
                                            <div class="d-flex gap-2">
                                                <a href='Clients.aspx?action=edit&id=<%# Eval("ClientId") %>'
                                                   class="btn btn-sm btn-outline-primary">עריכה</a>
                                                <a href='Logs.aspx?clientId=<%# Eval("ClientId") %>'
                                                   class="btn btn-sm btn-outline-info">לוגים</a>
                                                <asp:Button runat="server"
                                                    Text='<%# (bool)Eval("IsActive") ? "השבת" : "הפעל" %>'
                                                    CommandName="Toggle"
                                                    CommandArgument='<%# Eval("ClientId") %>'
                                                    CssClass='<%# (bool)Eval("IsActive") ? "btn btn-sm btn-warning" : "btn btn-sm btn-outline-secondary" %>'
                                                    CausesValidation="false" />
                                                <asp:Button runat="server" Text="מחק"
                                                    CommandName="Delete"
                                                    CommandArgument='<%# Eval("ClientId") %>'
                                                    CssClass="btn btn-sm btn-danger"
                                                    CausesValidation="false"
                                                    OnClientClick="return confirm('למחוק לקוח זה וכל הלוגים שלו?')" />
                                            </div>
                                        </div>
                                    </td>
                                </tr>
                            </ItemTemplate>
                        </asp:Repeater>
                        <asp:PlaceHolder ID="phEmpty" runat="server" Visible="false">
                            <tr>
                                <td colspan="7" class="empty-state">
                                    אין לקוחות עדיין. לחץ <a href="Clients.aspx?action=new">הוסף לקוח</a>.
                                </td>
                            </tr>
                        </asp:PlaceHolder>
                    </tbody>
                </table>
            </div>
        </div>
    </asp:Panel>

</asp:Content>
