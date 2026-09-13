<%--
    ============================================================================
    Underwriting work queue.

    A Web Forms page that hosts an ANGULAR module. This is the incremental
    modernization pattern for NEW functionality in a legacy shell: the page keeps
    the portal's master page, navigation and deployment, but everything inside
    <uw-workbench> is a standalone Angular 22 application that talks directly to
    the modern REST API (/api/underwriting). No postbacks, no ViewState, no
    UpdatePanel, and no code-behind logic beyond pointing the module at the API.

    Why not build this screen in Web Forms like its neighbours:
      * Its logic would have to be rewritten when the shell is retired. As an
        Angular module against /api, it moves to any future host unchanged.
      * A master-detail queue that re-renders one panel on click is exactly what
        UpdatePanel does badly (compare QuoteStatus.aspx).
      * It is unit-testable without IIS. See PolicyFlow.Legacy.Underwriting.

    Source: src/legacy/PolicyFlow.Legacy.Underwriting (Angular workspace).
    Build output: Scripts/underwriting/ (npm run build; not committed).

    Two integration details worth knowing, both handled in the module:
      * The master page wraps content in <form runat="server">. Enter in any text
        input would submit it and post the whole page back, so the module
        suppresses implicit submission on its own inputs.
      * Angular's review <form> is created through the DOM API inside that server
        form. Nested forms cannot be written in HTML markup but work when created
        this way, and Angular cancels the native submit event.
    ============================================================================
--%>
<%@ Page Language="C#"
         MasterPageFile="~/Site.master"
         AutoEventWireup="true"
         CodeBehind="Underwriting.aspx.cs"
         Inherits="PolicyFlow.Legacy.WebForms.Underwriting"
         Title="Underwriting" %>

<asp:Content ID="HeadContent" ContentPlaceHolderID="HeadContent" runat="server">
    <% if (IsModuleBuilt) { %>
    <link rel="stylesheet" href="<%: ModuleStylesUrl %>" />
    <% } %>
</asp:Content>

<asp:Content ID="MainContent" ContentPlaceHolderID="MainContent" runat="server">

    <asp:Panel ID="pnlModuleMissing" runat="server" Visible="false" CssClass="legacy-error">
        UNDERWRITING MODULE NOT BUILT.
        <ul>
            <li>cd src\legacy\PolicyFlow.Legacy.Underwriting</li>
            <li>npm ci</li>
            <li>npm run build &nbsp;(writes Scripts\underwriting\ in this project)</li>
        </ul>
    </asp:Panel>

    <asp:PlaceHolder ID="phModule" runat="server">
        <%-- The API address comes from the same appSetting the Web Forms pages use,
             HTML-encoded by <%: %>, so one Web.config value points both at the
             same environment. --%>
        <uw-workbench data-api-base="<%: ApiBaseAddress %>"></uw-workbench>

        <noscript>
            <div class="legacy-error">The underwriting work queue requires JavaScript.</div>
        </noscript>

        <%-- type="module": the Angular build emits ES modules, which also defer
             execution until the page has parsed, so <uw-workbench> exists first. --%>
        <script type="module" src="<%: ModuleScriptUrl %>"></script>
    </asp:PlaceHolder>

</asp:Content>
