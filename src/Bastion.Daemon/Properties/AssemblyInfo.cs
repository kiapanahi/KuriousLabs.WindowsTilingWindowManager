using System.Runtime.CompilerServices;

// Classic attribute form per docs/engineering/quality-gates.md §7 — bare assembly name,
// unsigned (Bastion ships no strong-named assemblies). First production consumer of this
// assembly's internal types (GitHub issue #9's config-loading subsystem) — everything it defines
// stays `internal`, matching Bastion.Win32's identical convention.
[assembly: InternalsVisibleTo("Bastion.Daemon.Tests")]
