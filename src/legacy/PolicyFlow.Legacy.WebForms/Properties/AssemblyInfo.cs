using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("PolicyFlow Legacy Agent Portal")]
[assembly: AssemblyDescription(
    "Legacy ASP.NET Web Forms agent portal. Retained in production during " +
    "modernization; calls the modern PolicyFlow API for all quoting and holds " +
    "no business logic of its own.")]
[assembly: AssemblyCompany("PolicyFlow (vendor-neutral demo)")]
[assembly: AssemblyProduct("PolicyFlow POC")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]
[assembly: Guid("7a3e9b41-2c58-4d6e-9f17-8b4c5d2e1a93")]

// Version deliberately looks like a long-lived line-of-business application that
// has been maintained for years. That is the realistic starting point for a
// modernization: not a greenfield project, but something with history.
[assembly: AssemblyVersion("2.4.1.0")]
[assembly: AssemblyFileVersion("2.4.1.0")]
