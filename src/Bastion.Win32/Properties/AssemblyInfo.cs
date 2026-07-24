using System.Runtime.CompilerServices;

// Classic attribute form per docs/engineering/quality-gates.md §7 — bare assembly name,
// unsigned (Bastion ships no strong-named assemblies).
[assembly: InternalsVisibleTo("Bastion.Win32.Tests")]

// GitHub issue #8: bastionc's `restore-windows` command must work even when bastiond is not
// running (DESIGN.md §3.7), so it directly constructs and calls the journal-restore types below
// rather than going through IPC — the same "internal types, InternalsVisibleTo per production
// consumer" shape this assembly already uses for its test project, extended to its first
// production consumer. Everything in this assembly stays `internal`; only Bastion.Cli.csproj's own
// ProjectReference + this attribute grant it access, matching the adapter-ring boundary
// (CLAUDE.md §3: "no HWND ever enters Bastion.Core or Bastion.Layout" — Bastion.Cli is not Core or
// Layout, and the journal restore path is exactly the kind of Win32-touching code that belongs in
// this assembly per that same boundary).
//
// The name below is "bastionc", not "Bastion.Cli" -- InternalsVisibleTo matches the compiled
// assembly name (Bastion.Cli.csproj's own <AssemblyName>bastionc</AssemblyName>), not the project
// or folder name; using the project name here would silently fail to grant access.
[assembly: InternalsVisibleTo("bastionc")]
