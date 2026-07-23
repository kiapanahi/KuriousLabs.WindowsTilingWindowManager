using System.Runtime.CompilerServices;

// Classic attribute form per docs/engineering/quality-gates.md §7 — bare assembly name,
// unsigned (Bastion ships no strong-named assemblies).
[assembly: InternalsVisibleTo("Bastion.Layout.Tests")]
