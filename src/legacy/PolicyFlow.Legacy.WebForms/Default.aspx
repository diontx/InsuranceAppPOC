<%@ Page Language="C#"
         MasterPageFile="~/Site.master"
         AutoEventWireup="true"
         CodeBehind="Default.aspx.cs"
         Inherits="PolicyFlow.Legacy.WebForms._Default"
         Title="Home" %>

<asp:Content ID="MainContent" ContentPlaceHolderID="MainContent" runat="server">

    <div class="legacy-panel">
        <div class="legacy-panel-header">AGENT PORTAL</div>
        <div class="legacy-panel-body">
            <p>
                This is the legacy ASP.NET Web Forms agent portal. It remains in
                production while the modern platform is rolled out, and it now
                calls the modern PolicyFlow API for all quoting.
            </p>

            <table class="legacy-form" cellpadding="4" cellspacing="0">
                <tr>
                    <td class="legacy-label">API status:</td>
                    <td><asp:Label ID="lblApiStatus" runat="server" CssClass="legacy-value-strong" /></td>
                </tr>
                <tr>
                    <td class="legacy-label">Carrier panel:</td>
                    <td><asp:Label ID="lblCarrierCount" runat="server" CssClass="legacy-value" /></td>
                </tr>
            </table>

            <div class="legacy-actions">
                <asp:HyperLink ID="lnkNewQuote" runat="server"
                               NavigateUrl="~/QuoteEntry.aspx"
                               CssClass="legacy-button legacy-button-primary"
                               Text="NEW QUOTE" />
                <asp:HyperLink ID="lnkStatusInquiry" runat="server"
                               NavigateUrl="~/QuoteStatus.aspx"
                               CssClass="legacy-button"
                               Text="STATUS INQUIRY" />
                <asp:HyperLink ID="lnkUnderwritingQueue" runat="server"
                               NavigateUrl="~/Underwriting.aspx"
                               CssClass="legacy-button"
                               Text="UNDERWRITING QUEUE" />
            </div>
        </div>
    </div>

    <div class="legacy-panel">
        <div class="legacy-panel-header">MODERNIZATION NOTICE</div>
        <div class="legacy-panel-body">
            <p>
                Quote pricing is no longer calculated in this application. Rating,
                eligibility and carrier selection are performed by the modern
                PolicyFlow platform and reached through its compatibility
                endpoints, so both channels always agree.
            </p>
            <p>
                The full carrier comparison &mdash; every priced option with limits,
                discounts and the rating calculation &mdash; is available only in the
                modern interface. This portal shows the headline premium.
            </p>
            <p class="legacy-note">
                See <span class="legacy-value-mono">docs/LEGACY-MODERNIZATION.md</span>
                for the migration strategy and the current retirement plan.
            </p>
        </div>
    </div>

</asp:Content>
