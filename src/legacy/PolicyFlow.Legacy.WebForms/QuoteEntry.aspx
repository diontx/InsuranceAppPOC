<%--
    ============================================================================
    Legacy quote entry page (ASP.NET Web Forms).

    This is a genuine Web Forms page: a server-side <form runat="server">, server
    controls, validators, and a postback model. It is the "before" side of the
    modernization.

    What it demonstrates about Web Forms:

      * ViewState. The hidden __VIEWSTATE field round-trips control state on every
        postback. On a page like this it is a few KB; on a page with a populated
        GridView it can reach hundreds of KB, sent up AND down on every click.
      * Postbacks. Every button is a full-page POST back to the same URL, which
        re-runs the entire page lifecycle. There is no partial update.
      * Code-behind. Event handlers live in QuoteEntry.aspx.cs, coupled to the
        page lifecycle and therefore very hard to unit test.

    What has CHANGED as part of modernization: the code-behind no longer contains
    any rating logic. It gathers input and calls the modern REST API. See
    QuoteEntry.aspx.cs and docs/LEGACY-MODERNIZATION.md.
    ============================================================================
--%>
<%@ Page Language="C#"
         MasterPageFile="~/Site.master"
         AutoEventWireup="true"
         CodeBehind="QuoteEntry.aspx.cs"
         Inherits="PolicyFlow.Legacy.WebForms.QuoteEntry"
         Title="Quote Entry" %>

<asp:Content ID="MainContent" ContentPlaceHolderID="MainContent" runat="server">

    <div class="legacy-panel">
        <div class="legacy-panel-header">
            AUTO QUOTE ENTRY
        </div>

        <div class="legacy-panel-body">

            <%-- Validation summary. Server-side validation, so the page must post
                 back before the user learns anything is wrong. --%>
            <asp:ValidationSummary
                ID="valSummary"
                runat="server"
                CssClass="legacy-error"
                HeaderText="Please correct the following:"
                DisplayMode="BulletList" />

            <asp:Panel ID="pnlServiceError" runat="server" Visible="false" CssClass="legacy-error">
                <asp:Label ID="lblServiceError" runat="server" />
            </asp:Panel>

            <table class="legacy-form" cellpadding="4" cellspacing="0">
                <tr>
                    <td class="legacy-label">
                        <asp:Label ID="lblInsuredName" runat="server"
                                   AssociatedControlID="txtInsuredName" Text="Insured Name:" />
                    </td>
                    <td>
                        <asp:TextBox ID="txtInsuredName" runat="server"
                                     MaxLength="120" CssClass="legacy-input" Width="260px" />
                        <asp:RequiredFieldValidator
                            ID="reqInsuredName" runat="server"
                            ControlToValidate="txtInsuredName"
                            ErrorMessage="Insured name is required."
                            Text="*" CssClass="legacy-error-marker" />
                    </td>
                </tr>

                <tr>
                    <td class="legacy-label">
                        <asp:Label ID="lblRiskState" runat="server"
                                   AssociatedControlID="ddlRiskState" Text="Risk State:" />
                    </td>
                    <td>
                        <%-- Populated in Page_Load from the modern /api/carriers
                             data where available, so the list cannot drift from
                             what the carriers actually write. --%>
                        <asp:DropDownList ID="ddlRiskState" runat="server" CssClass="legacy-input">
                            <asp:ListItem Value="GA" Text="GA - Georgia" />
                            <asp:ListItem Value="FL" Text="FL - Florida" />
                            <asp:ListItem Value="TX" Text="TX - Texas" />
                            <asp:ListItem Value="NC" Text="NC - North Carolina" />
                            <asp:ListItem Value="SC" Text="SC - South Carolina" />
                            <asp:ListItem Value="TN" Text="TN - Tennessee" />
                            <asp:ListItem Value="AL" Text="AL - Alabama" />
                            <asp:ListItem Value="VA" Text="VA - Virginia" />
                            <asp:ListItem Value="MS" Text="MS - Mississippi" />
                        </asp:DropDownList>
                    </td>
                </tr>

                <tr>
                    <td class="legacy-label">
                        <asp:Label ID="lblVehYear" runat="server"
                                   AssociatedControlID="txtVehYear" Text="Veh Year:" />
                    </td>
                    <td>
                        <asp:TextBox ID="txtVehYear" runat="server"
                                     MaxLength="4" CssClass="legacy-input" Width="80px" />
                        <asp:RequiredFieldValidator
                            ID="reqVehYear" runat="server"
                            ControlToValidate="txtVehYear"
                            ErrorMessage="Veh year is required."
                            Text="*" CssClass="legacy-error-marker" />
                        <%-- RangeValidator with Type="Integer": the classic Web
                             Forms way to enforce a numeric range without writing
                             any code. --%>
                        <asp:RangeValidator
                            ID="rngVehYear" runat="server"
                            ControlToValidate="txtVehYear"
                            Type="Integer" MinimumValue="1981" MaximumValue="2027"
                            ErrorMessage="Veh year must be between 1981 and 2027."
                            Text="*" CssClass="legacy-error-marker" />
                    </td>
                </tr>

                <tr>
                    <td class="legacy-label">
                        <asp:Label ID="lblVehMake" runat="server"
                                   AssociatedControlID="txtVehMake" Text="Veh Make:" />
                    </td>
                    <td>
                        <asp:TextBox ID="txtVehMake" runat="server"
                                     MaxLength="50" CssClass="legacy-input" Width="180px" />
                        <asp:RequiredFieldValidator
                            ID="reqVehMake" runat="server"
                            ControlToValidate="txtVehMake"
                            ErrorMessage="Veh make is required."
                            Text="*" CssClass="legacy-error-marker" />
                    </td>
                </tr>

                <tr>
                    <td class="legacy-label">
                        <asp:Label ID="lblVehModel" runat="server"
                                   AssociatedControlID="txtVehModel" Text="Veh Model:" />
                    </td>
                    <td>
                        <asp:TextBox ID="txtVehModel" runat="server"
                                     MaxLength="50" CssClass="legacy-input" Width="180px" />
                        <asp:RequiredFieldValidator
                            ID="reqVehModel" runat="server"
                            ControlToValidate="txtVehModel"
                            ErrorMessage="Veh model is required."
                            Text="*" CssClass="legacy-error-marker" />
                    </td>
                </tr>

                <tr>
                    <td class="legacy-label">
                        <asp:Label ID="lblMileage" runat="server"
                                   AssociatedControlID="txtMileage" Text="Annual Mileage:" />
                    </td>
                    <td>
                        <asp:TextBox ID="txtMileage" runat="server"
                                     MaxLength="9" CssClass="legacy-input" Width="100px" />
                        <asp:RequiredFieldValidator
                            ID="reqMileage" runat="server"
                            ControlToValidate="txtMileage"
                            ErrorMessage="Annual mileage is required."
                            Text="*" CssClass="legacy-error-marker" />
                        <span class="legacy-hint">(thousands separator allowed)</span>
                    </td>
                </tr>

                <tr>
                    <td class="legacy-label">
                        <asp:Label ID="lblPriorCov" runat="server"
                                   AssociatedControlID="txtPriorCovMonths" Text="Prior Cov Months:" />
                    </td>
                    <td>
                        <asp:TextBox ID="txtPriorCovMonths" runat="server"
                                     MaxLength="3" CssClass="legacy-input" Width="80px" />
                        <asp:RequiredFieldValidator
                            ID="reqPriorCov" runat="server"
                            ControlToValidate="txtPriorCovMonths"
                            ErrorMessage="Prior cov months is required."
                            Text="*" CssClass="legacy-error-marker" />
                        <asp:RangeValidator
                            ID="rngPriorCov" runat="server"
                            ControlToValidate="txtPriorCovMonths"
                            Type="Integer" MinimumValue="0" MaximumValue="600"
                            ErrorMessage="Prior cov months must be between 0 and 600."
                            Text="*" CssClass="legacy-error-marker" />
                        <span class="legacy-hint">(0 = no prior insurance, requires review)</span>
                    </td>
                </tr>
            </table>

            <div class="legacy-actions">
                <%-- A full-page postback. Every click re-runs the page lifecycle
                     and round-trips ViewState in both directions. --%>
                <asp:Button ID="btnSubmit" runat="server"
                            Text="SUBMIT QUOTE"
                            CssClass="legacy-button legacy-button-primary"
                            OnClick="btnSubmit_Click" />

                <asp:Button ID="btnClear" runat="server"
                            Text="CLEAR"
                            CssClass="legacy-button"
                            CausesValidation="false"
                            OnClick="btnClear_Click" />
            </div>

        </div>
    </div>

    <%-- Result panel. Made visible on a successful postback; its Visible state is
         held in ViewState across subsequent postbacks. --%>
    <asp:Panel ID="pnlResult" runat="server" Visible="false" CssClass="legacy-panel">
        <div class="legacy-panel-header">QUOTE SUBMITTED</div>
        <div class="legacy-panel-body">
            <table class="legacy-form" cellpadding="4" cellspacing="0">
                <tr>
                    <td class="legacy-label">Quote Nbr:</td>
                    <td><asp:Label ID="lblQuoteNbr" runat="server" CssClass="legacy-value-strong" /></td>
                </tr>
                <tr>
                    <td class="legacy-label">Status:</td>
                    <td><asp:Label ID="lblQuoteStatus" runat="server" CssClass="legacy-value" /></td>
                </tr>
                <tr>
                    <td class="legacy-label">Correlation ID:</td>
                    <td><asp:Label ID="lblCorrelationId" runat="server" CssClass="legacy-value-mono" /></td>
                </tr>
            </table>

            <div class="legacy-actions">
                <asp:HyperLink ID="lnkStatus" runat="server"
                               CssClass="legacy-button legacy-button-primary"
                               Text="CHECK QUOTE STATUS" />
            </div>

            <p class="legacy-note">
                Underwriting runs asynchronously in the modern platform. The quote
                is accepted immediately and priced in the background against the
                carrier panel.
            </p>
        </div>
    </asp:Panel>

</asp:Content>
