using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System;
using System.CommandLine;
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

            cmd.Subcommands.Add(BuildRefresh(services));
            cmd.Subcommands.Add(BuildListSources(services));

            return cmd;
        }

        private static Command BuildRefresh(IServiceProvider services)
        {
            var cmd = new Command("refresh", "Force-refresh the gallery cache from all remote sources.");

            cmd.SetAction(async (parseResult, ct) =>
                await RunRefreshAsync(services, ct));

            return cmd;
        }

        private static async Task<int> RunRefreshAsync(IServiceProvider services, CancellationToken ct)
        {
            var galleryService = services.GetRequiredService<IGalleryService>();

            var all = new System.Collections.Generic.List<GalleryItem>();
            var seen = new System.Collections.Generic.HashSet<(string, string)>();

            // Redirected stdout: line output only. AnsiConsole.Status() drives
            // the same Live display machinery as create's progress table and
            // crashes on a non-console handle otherwise.
            if (Console.IsOutputRedirected)
            {
                await galleryService.LoadFromSourcesStreamingAsync(seen, batch =>
                {
                    all.AddRange(batch);
                    Console.WriteLine($"Loaded {all.Count} images...");
                }, ct);
            }
            else
            {
                await AnsiConsole.Status()
                    .StartAsync("Refreshing gallery from remote sources...", async statusCtx =>
                    {
                        await galleryService.LoadFromSourcesStreamingAsync(seen, batch =>
                        {
                            all.AddRange(batch);
                            statusCtx.Status($"Loaded {all.Count} images...");
                        }, ct);
                    });
            }

            galleryService.SaveCache(all);
            int count = all.Count;

            if (Console.IsOutputRedirected)
                Console.WriteLine($"Gallery refreshed: {count} images cached.");
            else
                AnsiConsole.MarkupLine($"[green]✓[/] Gallery refreshed: {count} images cached.");
            return ExitCodes.Success;
        }

        private static Command BuildListSources(IServiceProvider services)
        {
            var cmd = new Command("list-sources", "List registered gallery source types.");

            cmd.SetAction(parseResult =>
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

                return ExitCodes.Success;
            });

            return cmd;
        }
    }
}
