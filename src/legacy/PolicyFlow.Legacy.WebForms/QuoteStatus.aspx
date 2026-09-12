<%--
    ============================================================================
    Legacy quote status page.

    Polls the modern API for the underwriting outcome.

    The polling mechanism is deliberately period-accurate: an <asp:Timer> inside
    an <asp:UpdatePanel>, which is how a Web Forms application did partial page
    updates before fetch and SPAs existed. It is worth understanding what that
    actually costs, because it is the clearest single illustration of why this
    architecture was replaced:

      * Each tick is a full ASP.NET postback. The entire page lifecycle runs.
      * ViewState is serialised up AND back down on every tick, every 2 seconds.
      * The server re-creates the whole control tree to render one label.

    The React SPA does the same job with a 1.5-second fetch of a single indexed
    row and re-renders one component. Same user experience, a fraction of the
    bytes and server work.
    ============================================================================
--%>
<%@ Page Language="C#"
         MasterPageFile="~/Site.master"
         AutoEventWireup="true"
         CodeBehind="QuoteStatus.aspx.cs"
         Inherits="PolicyFlow.Legacy.WebForms.QuoteStatus"
         Title="Quote Status" %>

<asp:Content ID="MainContent" ContentPlaceHolderID="MainContent" runat="server">

    <%-- ScriptManager is required for UpdatePanel and Timer. It injects the
         ASP.NET AJAX client library into the page. --%>
    <asp:ScriptManager ID="scriptManager" runat="server" EnablePartialRendering="true" />

    <div class="legacy-panel">
        <div class="legacy-panel-header">QUOTE STATUS INQUIRY</div>

        <div class="legacy-panel-body">

            <table class="legacy-form" cellpadding="4" cellspacing="0">
                <tr>
                    <td class="legacy-label">
                        <asp:Label ID="lblQuoteNbrPrompt" runat="server"
                                   AssociatedControlID="txtQuoteNbr" Text="Quote Nbr:" />
                    </td>
                    <td>
                        <asp:TextBox ID="txtQuoteNbr" runat="server"
                                     MaxLength="20" CssClass="legacy-input legacy-input-mono"
                                     Width="180px" />
                        <asp:RequiredFieldValidator
                            ID="reqQuoteNbr" runat="server"
                            ControlToValidate="txtQuoteNbr"
                            ErrorMessage="Quote nbr is required."
                            Text="* required" CssClass="legacy-error-marker" />
                        <asp:Button ID="btnLookup" runat="server"
                                    Text="LOOKUP"
                                    CssClass="legacy-button legacy-button-primary"
                                    OnClick="btnLookup_Click" />
                    </td>
                </tr>
            </table>

        </div>
    </div>

    <%-- UpdateMode="Conditional" so the panel refreshes only when the timer or
         the lookup button triggers it, rather than on every postback anywhere on
         the page. --%>
    <asp:UpdatePanel ID="updResult" runat="server" UpdateMode="Conditional">
        <Triggers>
            <asp:AsyncPostBackTrigger ControlID="pollTimer" EventName="Tick" />
            <asp:AsyncPostBackTrigger ControlID="btnLookup" EventName="Click" />
        </Triggers>
        <ContentTemplate>

            <%-- Enabled only while the quote is non-terminal. The code-behind
                 disables it on Rated/Referral/Declined, which is the Web Forms
                 equivalent of the SPA's "stop polling at a terminal state". --%>
            <asp:Timer ID="pollTimer" runat="server"
                       Interval="2000"
                       Enabled="false"
                       OnTick="pollTimer_Tick" />

            <asp:Panel ID="pnlError" runat="server" Visible="false" CssClass="legacy-error">
                <asp:Label ID="lblError" runat="server" />
            </asp:Panel>

            <asp:Panel ID="pnlStatus" runat="server" Visible="false" CssClass="legacy-panel">
                <div class="legacy-panel-header">
                    QUOTE <asp:Label ID="lblQuoteNbrResult" runat="server" />
                </div>
                <div class="legacy-panel-body">

                    <table class="legacy-form" cellpadding="4" cellspacing="0">
                        <tr>
                            <td class="legacy-label">Status:</td>
                            <td>
                                <asp:Label ID="lblStatus" runat="server" CssClass="legacy-value-strong" />
                                <asp:Label ID="lblPollingIndicator" runat="server"
                                           CssClass="legacy-hint" Visible="false"
                                           Text="(underwriting in progress, refreshing every 2s...)" />
                            </td>
                        </tr>
                        <tr>
                            <td class="legacy-label">Annual Prem:</td>
                            <td><asp:Label ID="lblAnnualPrem" runat="server" CssClass="legacy-value-mono" /></td>
                        </tr>
                        <tr>
                            <td class="legacy-label">Best Carrier:</td>
                            <td><asp:Label ID="lblBestCarrier" runat="server" CssClass="legacy-value" /></td>
                        </tr>
                        <tr>
                            <td class="legacy-label">Offers:</td>
                            <td><asp:Label ID="lblOfferCnt" runat="server" CssClass="legacy-value" /></td>
                        </tr>
                        <tr id="rowDecision" runat="server" visible="false">
                            <td class="legacy-label">Decision:</td>
                            <td><asp:Label ID="lblDecisionMsg" runat="server" CssClass="legacy-value" /></td>
                        </tr>
                        <tr>
                            <td class="legacy-label">Correlation ID:</td>
                            <td><asp:Label ID="lblCorrelationId" runat="server" CssClass="legacy-value-mono" /></td>
                        </tr>
                        <tr>
                            <td class="legacy-label">Polls:</td>
                            <td><asp:Label ID="lblPollCount" runat="server" CssClass="legacy-value" /></td>
                        </tr>
                    </table>

                    <asp:Panel ID="pnlRatedNote" runat="server" Visible="false">
                        <p class="legacy-note">
                            The full carrier comparison — every priced option with limits,
                            discounts and the rating calculation — is available in the modern
                            interface. This legacy screen shows only the headline premium,
                            because its database schema has no place to store fifteen offers.
                            That is a reason to retire the screen, not to extend it.
                        </p>
                    </asp:Panel>

                </div>
            </asp:Panel>

        </ContentTemplate>
    </asp:UpdatePanel>

</asp:Content>
