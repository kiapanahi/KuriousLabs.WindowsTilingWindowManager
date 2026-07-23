using Bastion.Daemon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// TODO(DESIGN.md §3.9): register the real composition graph here — WinEvent ingest pump,
// Coalescer, Reconciler, Placement Executor, the named-pipe IPC command/broadcast servers, and
// JSONC config loader. BastiondService is a placeholder proving the Generic Host + NativeAOT
// wiring builds and runs; it owns none of that yet.
builder.Services.AddHostedService<BastiondService>();

using IHost host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
