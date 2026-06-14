using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VMCreate.Gallery;

namespace VMCreate.CLI.Commands
{
    public static class ListCommand
    {
        public static Command Build(IServiceProvider services)
        {
            var cmd = new Command("list", "List available gallery images.");

            var categoryOpt = new Option<string>(
                "--category",
                getDefaultValue: () => "all",
                description: "Filter by category: security, general, or all.");

            var showIsoOpt = new Option<bool>(
                "--show-iso",
                getDefaultValue: () => false,
                description: "Include ISO installer images (require manual OS installation).");

            var filterOpt = new Option<string>(
                "--filter",
                description: "Full-text search across name, publisher, and description.");

            var formatOpt = new Option<string>(
                "--format",
                getDefaultValue: () => "table",
                description: "Output format: table, json, or csv.");

            var noCacheOpt = new Option<bool>(
                "--no-cache",
                getDefaultValue: () => false,
                description: "Force refresh from remote sources, ignoring local cache.");

            cmd.AddOption(categoryOpt);
            cmd.AddOption(showIsoOpt);
            cmd.AddOption(filterOpt);
            cmd.AddOption(formatOpt);
            cmd.AddOption(noCacheOpt);

            cmd.SetHandler(async (InvocationContext ctx) =>
            {
                var category = ctx.ParseResult.GetValueForOption(categoryOpt);
                var showIso = ctx.ParseResult.GetValueForOption(showIsoOpt);
                var filter = ctx.ParseResult.GetValueForOption(filterOpt);
                var format = ctx.ParseResult.GetValueForOption(formatOpt);
                var noCache = ctx.ParseResult.GetValueForOption(noCacheOpt);
                var ct = ctx.GetCancellationToken();

                ctx.ExitCode = await RunAsync(services, category, showIso, filter, format, noCache, ct);
            });

            return cmd;
        }

        private static async Task<int> RunAsync(
            IServiceProvider services,
            string category,
            bool showIso,
            string filter,
            string format,
            bool noCache,
            CancellationToken ct)
        {
            var galleryService = services.GetRequiredService<IGalleryService>();

            List<GalleryItem> items;

            if (!noCache)
            {
                items = galleryService.LoadFromCache();
                if (items.Count == 0)
                    items = await LoadFromSourcesAsync(galleryService, ct);
            }
            else
            {
                items = await LoadFromSourcesAsync(galleryService, ct);
            }

            // Apply filters
            if (!showIso)
                items = items.Where(i => !i.FileType.Equals("ISO", StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.Equals(category, "all", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(category, "security", StringComparison.OrdinalIgnoreCase))
                    items = items.Where(i => i.IsSecurity).ToList();
                else if (string.Equals(category, "general", StringComparison.OrdinalIgnoreCase))
                    items = items.Where(i => !i.IsSecurity).ToList();
            }

            if (!string.IsNullOrEmpty(filter))
            {
                items = items.Where(i =>
                    Contains(i.Name, filter) ||
                    Contains(i.Publisher, filter) ||
                    Contains(i.Description, filter)).ToList();
            }

            // Render
            switch (format?.ToLowerInvariant())
            {
                case "json":
                    RenderJson(items);
                    break;
                case "csv":
                    RenderCsv(items);
                    break;
                default:
                    RenderTable(items);
                    break;
            }

            return ExitCodes.Success;
        }

        private static async Task<List<GalleryItem>> LoadFromSourcesAsync(IGalleryService galleryService, CancellationToken ct)
        {
            var all = new List<GalleryItem>();
            var seen = new System.Collections.Generic.HashSet<(string, string)>();
            await galleryService.LoadFromSourcesStreamingAsync(seen, batch => all.AddRange(batch), ct);
            galleryService.SaveCache(all);
            return all;
        }

        private static bool Contains(string source, string value) =>
            source != null && source.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0;

        private static void RenderTable(List<GalleryItem> items)
        {
            var table = new Table();
            table.Border(TableBorder.Rounded);
            table.AddColumn("[bold]Name[/]");
            table.AddColumn("[bold]Publisher[/]");
            table.AddColumn("[bold]Type[/]");
            table.AddColumn("[bold]Category[/]");
            table.AddColumn("[bold]Version[/]");
            table.AddColumn("[bold]★[/]");

            foreach (var item in items)
            {
                table.AddRow(
                    Markup.Escape(item.Name ?? string.Empty),
                    Markup.Escape(item.Publisher ?? string.Empty),
                    item.FileType ?? string.Empty,
                    item.IsSecurity ? "[red]Security[/]" : "General",
                    Markup.Escape(item.Version ?? string.Empty),
                    item.IsRecommended ? "[yellow]★[/]" : string.Empty);
            }

            AnsiConsole.Write(table);
            AnsiConsole.MarkupLine($"\n[grey]{items.Count} image(s)[/]");
        }

        private static void RenderJson(List<GalleryItem> items)
        {
            var opts = new JsonSerializerOptions
            {
                WriteIndented = false,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            };
            foreach (var item in items)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    name = item.Name,
                    publisher = item.Publisher,
                    description = item.Description,
                    version = item.Version,
                    fileType = item.FileType,
                    category = item.Category,
                    isRecommended = item.IsRecommended,
                    isPreInstalled = item.IsPreInstalled,
                    diskUri = item.DiskUri,
                }, opts));
            }
        }

        private static void RenderCsv(List<GalleryItem> items)
        {
            Console.WriteLine("Name,Publisher,Type,Category,Version,Recommended,DiskUri");
            foreach (var item in items)
            {
                Console.WriteLine(
                    $"{CsvEscape(item.Name)}," +
                    $"{CsvEscape(item.Publisher)}," +
                    $"{CsvEscape(item.FileType)}," +
                    $"{CsvEscape(item.Category)}," +
                    $"{CsvEscape(item.Version)}," +
                    $"{item.IsRecommended}," +
                    $"{CsvEscape(item.DiskUri)}");
            }
        }

        private static string CsvEscape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }
    }
}
