using System.Runtime.CompilerServices;

// Classic attribute form per docs/engineering/quality-gates.md §7 — bare assembly name, unsigned
// (Bastion ships no strong-named assemblies). Grants Bastion.TestWindows.Tests direct access to
// TestWindowOptions (GitHub issue #13) so its argument-parsing logic is unit-testable without
// spawning a real window — the only part of this project that is unit-testable at all without a
// live desktop session; TestWindowSpawner.Run/WndProc/WatchStdinForEof need one and are exercised
// only by the not-yet-built Tier 3 harness (DESIGN.md §11) this issue explicitly excludes.
[assembly: InternalsVisibleTo("Bastion.TestWindows.Tests")]
