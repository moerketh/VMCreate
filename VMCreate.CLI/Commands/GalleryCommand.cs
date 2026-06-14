using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.Gallery;

namespace VMCreate.CLI.Commands
{
    public static class GalleryCommand
    {
        public static Command Build(IServiceProvider services)
        {
            var cmd = new Command("gallery", "Manage gallery sources and cache.");

            cmd.AddCommand(BuildRefresh(services));
            cmd.AddCommand(BuildListSources(services));

            return cmd;
        }

        private static Command BuildRefresh(IServiceProvider services)
        {
            var cmd = new Command("refresh", "Force-refresh the gallery cache from all remote sources.");

            cmd.SetHandler(async (InvocationContext ctx) =>
            {
                var ct = ctx.GetCancellationToken();
                ctx.ExitCode = await RunRefreshAsync(services, ct);
            });

            return cmd;
        }

        private static async Task<int> RunRefreshAsync(IServiceProvider services, CancellationToken ct)
        {
            var galleryService = services.GetRequiredService<IGalleryService>();
            int count = 0;

            await AnsiConsole.Status()
                .StartAsync("Refreshing gallery from remote sources...", async statusCtx =>
                {
                    var all = new System.Collections.Generic.List<GalleryItem>();
                    var seen = new System.Collections.Generic.HashSet<(string, string)>();

                    await galleryService.LoadFromSourcesStreamingAsync(seen, batch =>
                    {
                        all.AddRange(batch);
                        statusCtx.Status($"Loaded {all.Count} images...");
                    }, ct);

                    galleryService.SaveCache(all);
                    count = all.Count;
                });

            AnsiConsole.MarkupLine($"[green]✓[/] Gallery refreshed: {count} images cached.");
            return ExitCodes.Success;
        }

        private static Command BuildListSources(IServiceProvider services)
        {
            var cmd = new Command("list-sources", "List registered gallery source types.");

            cmd.SetHandler((InvocationContext ctx) =>
            {
                // GetServices returns each individual loader plus the AggregateGalleryLoader at the end.
                // Show only the concrete per-source types (exclude the aggregate wrapper).
                var loaders = services.GetServices<IGalleryLoader>()
                    .Where(l => l is not AggregateGalleryLoader)
                    .ToList();

                int i = 0;
                foreach (var loader in loaders)
                    AnsiConsole.MarkupLine($"  [grey]{i++}.[/] {Markup.Escape(loader.GetType().Name)}");

                if (i == 0)
                    AnsiConsole.MarkupLine("[grey]No gallery sources registered.[/]");

                ctx.ExitCode = ExitCodes.Success;
                return Task.CompletedTask;
            });

            return cmd;
        }
    }
}
