using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using SharpCompress.Archives;
using SharpCompress.Common;
using System.Management.Automation;
using System.Collections.ObjectModel;
using System.Xml.Linq;
using System.Net;

namespace VMCreateVM
{
    public partial class MainWindow : Window
    {
        private readonly string logPath = Path.Combine(Path.GetTempPath(), "VMCreate.log");
        private readonly string zipFilePath = Path.Combine(Path.GetTempPath(), "vm_image.7z");
        private readonly string extractPath = Path.Combine(Path.GetTempPath(), "VMExtracted");
        private readonly string vmPath;
        private readonly ObservableCollection<GalleryItem> galleryItems = new ObservableCollection<GalleryItem>();

        public MainWindow()
        {
            vmPath = GetDefaultVirtualHardDiskPath();
            InitializeComponent();
            GalleryListBox.ItemsSource = galleryItems;
            LoadGalleryItems();
            SearchTextBox.TextChanged += SearchTextBox_TextChanged;
            GalleryListBox.SelectionChanged += GalleryListBox_SelectionChanged;
            CreateVMButton.Click += CreateVMButton_Click;
        }

        private string GetDefaultVirtualHardDiskPath()
        {
            string defaultPath = @"C:\ProgramData\Microsoft\Windows\Virtual Hard Disks";
            try
            {
                string registryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
                {
                    if (key != null)
                    {
                        string path = key.GetValue("DefaultVirtualHardDiskPath") as string;
                        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                        {
                            WriteLog($"Using DefaultVirtualHardDiskPath from registry: {path}");
                            return path;
                        }
                    }
                }
                WriteLog($"DefaultVirtualHardDiskPath not found or invalid. Using default: {defaultPath}");
            }
            catch (Exception ex)
            {
                WriteLog($"Error reading DefaultVirtualHardDiskPath: {ex.Message}");
            }
            return defaultPath;
        }

        private void WriteLog(string message)
        {
            try
            {
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\n");
            }
            catch { }
        }

        private async void LoadGalleryItems()
        {
            WriteLog("Loading gallery items.");
            try
            {
                string registryPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Virtualization";
                string[] locations = null;
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(registryPath))
                {
                    if (key != null)
                    {
                        locations = key.GetValue("GalleryLocations") as string[];
                    }
                }

                HashSet<string> loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<GalleryItem> newItems;

                if (locations == null || locations.Length == 0)
                {
                    WriteLog("GalleryLocations registry key not found or empty. Using default sources: Ubuntu XML, Microsoft JSON, local JSON.");
                }
                else
                {
                    WriteLog($"Found GalleryLocations: {string.Join(", ", locations)}");
                    foreach (string location in locations)
                    {
                        if (location.StartsWith("http"))
                        {
                            newItems = await LoadJsonFromUrl(location);
                            foreach (var item in newItems.Where(item => !loadedNames.Contains(item.Name)))
                            {
                                galleryItems.Add(item);
                                loadedNames.Add(item.Name);
                            }
                        }
                        else if (Directory.Exists(location))
                        {
                            newItems = LoadJsonFromFiles(location);
                            foreach (var item in newItems.Where(item => !loadedNames.Contains(item.Name)))
                            {
                                galleryItems.Add(item);
                                loadedNames.Add(item.Name);
                            }
                        }
                        else
                        {
                            WriteLog($"Invalid local path: {location}");
                        }
                    }
                }

                // Load local JSON file
                string localJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gallery.json");
                if (File.Exists(localJsonPath))
                {
                    WriteLog($"Loading local JSON from: {localJsonPath}");
                    newItems = LoadJsonFromFile(localJsonPath);
                    foreach (var item in newItems.Where(item => !loadedNames.Contains(item.Name)))
                    {
                        galleryItems.Add(item);
                        loadedNames.Add(item.Name);
                    }
                }
                else
                {
                    WriteLog($"Local JSON file not found: {localJsonPath}");
                }

                // Load Ubuntu from repo XML 
                newItems = await LoadXmlFromUrl("https://raw.githubusercontent.com/canonical/ubuntu-desktop-hyper-v/master/HyperVGallery/Ubuntu-24.04.xml");
                foreach (var item in newItems.Where(item => !loadedNames.Contains(item.Name)))
                {
                    galleryItems.Add(item);
                    loadedNames.Add(item.Name);
                }
                // Load Microsoft Default gallery items
                newItems = await LoadJsonFromUrl("https://go.microsoft.com/fwlink/?linkid=851584");
                foreach (var item in newItems.Where(item => !loadedNames.Contains(item.Name)))
                {
                    galleryItems.Add(item);
                    loadedNames.Add(item.Name);
                }

                if (galleryItems.Count == 0)
                {
                    WriteLog("No gallery items loaded from any source.");
                    MessageBox.Show("No gallery items could be loaded. Please check your configuration.", "Warning", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                WriteLog($"Populated ListBox with {galleryItems.Count} items.");
            }
            catch (Exception ex)
            {
                WriteLog($"Failed to load gallery items: {ex.Message}");
                MessageBox.Show($"Failed to load gallery items: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<GalleryItem> LoadJsonFromFile(string path)
        {
            var items = new List<GalleryItem>();
            try
            {
                WriteLog($"Parsing local JSON file: {path}");
                string json = File.ReadAllText(path);
                items.AddRange(ParseJson(json));
                WriteLog($"Parsed JSON from {path}");
            }
            catch (Exception ex)
            {
                WriteLog($"Failed to parse JSON from {path}: {ex.Message}");
            }
            return items;
        }

        private async Task<List<GalleryItem>> LoadJsonFromUrl(string url)
        {
            try
            {
                WriteLog($"Downloading JSON from {url}");
                using (HttpClient client = new HttpClient())
                {
                    string json = await client.GetStringAsync(url);
                    var items = ParseJson(json);
                    WriteLog($"Parsed JSON from {url}");
                    return items;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Failed to download or parse JSON from {url}: {ex.Message}");
                return new List<GalleryItem>();
            }
        }

        private List<GalleryItem> LoadJsonFromFiles(string path)
        {
            var items = new List<GalleryItem>();
            try
            {
                foreach (string file in Directory.GetFiles(path, "*.json"))
                {
                    items.AddRange(LoadJsonFromFile(file));
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Failed to parse JSON from {path}: {ex.Message}");
            }
            return items;
        }

        private async Task<List<GalleryItem>> LoadXmlFromUrl(string url)
        {
            var items = new List<GalleryItem>();
            try
            {
                WriteLog($"Downloading XML from {url}");
                using (HttpClient client = new HttpClient())
                {
                    string xml = await client.GetStringAsync(url);
                    var xdoc = XDocument.Parse(xml);
                    var images = new List<Dictionary<string, object>>();

                    var vhd = xdoc.Element("vhd");
                    if (vhd == null)
                    {
                        WriteLog("No vhd element found in XML");
                        return items;
                    }

                    var details = vhd.Element("details");
                    if (details == null)
                    {
                        WriteLog("No details element found in XML");
                        return items;
                    }

                    var descriptions = vhd.Element("descriptions")?.Elements("description").Select(d => d.Value).ToList() ?? new List<string>();
                    var image = vhd.Element("image");

                    if (image == null)
                    {
                        WriteLog("No image element found in XML");
                        return items;
                    }

                    var imageData = new Dictionary<string, object>
            {
                { "name", details.Element("name")?.Value ?? "" },
                { "publisher", details.Element("publisher")?.Value ?? "" },
                { "description", descriptions },
                { "version", image.Element("version")?.Value ?? "" },
                { "lastUpdated", details.Element("lastUpdated")?.Value ?? "" },
                { "thumbnail", new Dictionary<string, string> { { "uri", image.Element("thumbnail")?.Element("uri")?.Value ?? "" } } },
                { "logo", new Dictionary<string, string> { { "uri", image.Element("logo")?.Element("uri")?.Value ?? "" } } },
                { "disk", new Dictionary<string, string>
                    {
                        { "uri", image.Element("disk")?.Element("uri")?.Value ?? "" },
                        { "archiveRelativePath", image.Element("disk")?.Element("archiveRelativePath")?.Value ?? "" }
                    }
                },
                { "config", new Dictionary<string, string>
                    {
                        { "secureBoot", image.Element("secureBoot")?.Value ?? "" },
                        { "enhancedSessionTransportType", image.Element("enhancedSessionTransportType")?.Value ?? "" }
                    }
                }
            };

                    images.Add(imageData);

                    var json = JsonSerializer.Serialize(new { images }, new JsonSerializerOptions { WriteIndented = true });
                    items.AddRange(ParseJson(json));
                    WriteLog($"Parsed XML as JSON from {url}");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Failed to download or parse XML from {url}: {ex.Message}");
            }
            return items;
        }

        private List<GalleryItem> ParseJson(string json)
        {
            var items = new List<GalleryItem>();
            try
            {
                var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });
                foreach (var image in doc.RootElement.GetProperty("images").EnumerateArray())
                {
                    try
                    {
                        // Critical keys (required)
                        if (!image.TryGetProperty("name", out var nameProp) || !image.TryGetProperty("disk", out var diskProp) || !diskProp.TryGetProperty("uri", out var diskUriProp))
                        {
                            WriteLog("Skipping image: Missing critical keys (name or disk.uri)");
                            continue;
                        }

                        string name = nameProp.GetString();
                        string diskUri = diskUriProp.GetString();
                        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(diskUri))
                        {
                            WriteLog("Skipping image: Empty name or diskUri");
                            continue;
                        }

                        var item = new GalleryItem
                        {
                            Name = name,
                            Publisher = image.TryGetProperty("publisher", out var publisherProp) ? publisherProp.GetString() ?? "" : "",
                            Description = image.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.Array
                                ? string.Join(" ", descProp.EnumerateArray().Select(e => e.GetString())).Trim()
                                : (descProp.ValueKind == JsonValueKind.String ? descProp.GetString() : ""),
                            ThumbnailUri = image.TryGetProperty("thumbnail", out var thumbProp) && thumbProp.TryGetProperty("uri", out var thumbUriProp)
                                ? thumbUriProp.GetString() ?? ""
                                : "",
                            LogoUri = image.TryGetProperty("logo", out var logoProp) && logoProp.TryGetProperty("uri", out var logoUriProp)
                                ? logoUriProp.GetString() ?? ""
                                : "",
                            DiskUri = diskUri,
                            ArchiveRelativePath = diskProp.TryGetProperty("archiveRelativePath", out var archivePathProp)
                                ? archivePathProp.GetString() ?? ""
                                : "",
                            SecureBoot = image.TryGetProperty("config", out var configProp) && configProp.TryGetProperty("secureBoot", out var secureBootProp)
                                ? secureBootProp.GetString() ?? ""
                                : "",
                            EnhancedSessionTransportType = configProp.TryGetProperty("enhancedSessionTransportType", out var enhancedProp)
                                ? enhancedProp.GetString() ?? ""
                                : "",
                            Version = image.TryGetProperty("version", out var versionProp) ? versionProp.GetString() ?? "" : "",
                            LastUpdated = image.TryGetProperty("lastUpdated", out var lastUpdatedProp) ? lastUpdatedProp.GetString() ?? "" : ""
                        };

                        items.Add(item);
                    }
                    catch (KeyNotFoundException ex)
                    {
                        WriteLog($"Error parsing image: Missing key '{ex.Message}'");
                    }
                    catch (Exception ex)
                    {
                        WriteLog($"Error parsing image: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error parsing JSON: {ex.Message}");
            }
            return items;
        }

        private void SearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchTextBox.Text.ToLower();
            var filteredItems = galleryItems.Where(item =>
                item.Name.ToLower().Contains(filter) ||
                item.Publisher.ToLower().Contains(filter) ||
                item.Description.ToLower().Contains(filter)).ToList();
            GalleryListBox.ItemsSource = filteredItems;
            WriteLog($"Applied search filter: {filter}");
        }

        private void GalleryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (GalleryListBox.SelectedItem is GalleryItem selectedItem)
            {
                VMNameTextBox.Text = selectedItem.Name;
                DetailScreenshot.Source = new BitmapImage(new Uri(selectedItem.ThumbnailUri));
                DetailName.Text = $"Name: {selectedItem.Name}";
                DetailPublisher.Text = $"Publisher: {selectedItem.Publisher}";
                DetailVersion.Text = $"Version: {selectedItem.Version}";
                DetailLastUpdated.Text = $"Last Updated: {selectedItem.LastUpdated}";
                DetailDescription.Text = $"Description: {selectedItem.Description}";
                WriteLog($"Set default VM name to: {selectedItem.Name} and updated details panel");
            }
            else
            {
                DetailScreenshot.Source = null;
                DetailName.Text = "";
                DetailPublisher.Text = "";
                DetailVersion.Text = "";
                DetailLastUpdated.Text = "";
                DetailDescription.Text = "";
                WriteLog("No item selected in gallery, cleared details panel.");
            }
        }

        private async void CreateVMButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!(GalleryListBox.SelectedItem is GalleryItem selectedItem))
                {
                    WriteLog("No gallery item selected.");
                    throw new Exception("Please select a gallery item!");
                }
                WriteLog($"Selected item: {selectedItem.Name}");

                if (string.IsNullOrEmpty(VMNameTextBox.Text))
                {
                    WriteLog("VM name is empty.");
                    throw new Exception("VM Name is required!");
                }
                string vmName = VMNameTextBox.Text;

                if (!int.TryParse(MemoryTextBox.Text, out int memoryMB) || memoryMB < 512)
                {
                    WriteLog($"Invalid memory value: {MemoryTextBox.Text}");
                    throw new Exception("Memory must be at least 512 MB!");
                }

                if (!int.TryParse(CPUTextBox.Text, out int cpuCount) || cpuCount < 1)
                {
                    WriteLog($"Invalid CPU count: {CPUTextBox.Text}");
                    throw new Exception("CPU count must be at least 1!");
                }

                if (string.IsNullOrEmpty(selectedItem.DiskUri) || !selectedItem.DiskUri.StartsWith("http"))
                {
                    WriteLog($"Invalid Disk URI: {selectedItem.DiskUri}");
                    throw new Exception($"Invalid disk URI: {selectedItem.DiskUri}");
                }

                WriteLog($"Validated inputs: VMName={vmName}, Memory={memoryMB} MB, CPU={cpuCount}, DiskUri={selectedItem.DiskUri}");

                // Resolve mirror URI
                string mirrorUri = await ResolveMirrorUri(selectedItem.DiskUri);
                WriteLog($"Resolved mirror URI: {mirrorUri}");

                // Show progress window
                var progressWindow = new ProgressWindow { Owner = this };
                progressWindow.Show();
                progressWindow.SetStatus("Downloading...", mirrorUri);

                // Download file
                await DownloadFileAsync(mirrorUri, zipFilePath, progressWindow);

                if (progressWindow.IsCancelled)
                {
                    WriteLog("Download cancelled.");
                    progressWindow.Close();
                    return;
                }

                // Unpack 7zip
                progressWindow.SetStatus("Unpacking...", null);
                await Unpack7ZipAsync(zipFilePath, extractPath, progressWindow);

                // Create VM
                progressWindow.SetStatus("Creating VM...", null);
                await CreateVMAsync(vmName, memoryMB, cpuCount, selectedItem, extractPath, progressWindow);

                progressWindow.Close();

                // Show success window
                var successWindow = new SuccessWindow { Owner = this };
                successWindow.ShowDialog();
                WriteLog("Displayed success window.");
            }
            catch (Exception ex)
            {
                WriteLog($"Error in Create VM: {ex.Message}");
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                CleanupTempFiles();
            }
        }

        private async Task<string> ResolveMirrorUri(string uri)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "VMCreateVM");
                    var request = new HttpRequestMessage(HttpMethod.Head, uri);
                    var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                    if (response.StatusCode == System.Net.HttpStatusCode.Found || response.StatusCode == System.Net.HttpStatusCode.Moved)
                    {
                        return response.Headers.Location.ToString();
                    }
                    return uri;
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error resolving mirror URI: {ex.Message}");
                return uri;
            }
        }

        private async Task DownloadFileAsync(string uri, string filePath, ProgressWindow progressWindow)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "VMCreateVM");
                    DateTime startTime = DateTime.Now;
                    using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
                    {
                        response.EnsureSuccessStatusCode();
                        long? contentLength = response.Content.Headers.ContentLength;
                        WriteLog($"Content-Length: {contentLength} bytes");

                        using (var contentStream = await response.Content.ReadAsStreamAsync())
                        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                        {
                            long totalBytesRead = 0;
                            byte[] buffer = new byte[65536];
                            int bytesRead;
                            DateTime lastUpdate = DateTime.Now;
                            long lastBytesRead = 0;

                            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0 && !progressWindow.IsCancelled)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalBytesRead += bytesRead;

                                if ((DateTime.Now - lastUpdate).TotalMilliseconds >= 1000)
                                {
                                    double progress = contentLength.HasValue ? (double)totalBytesRead / contentLength.Value * 100 : 0;
                                    double speedMBps = ((totalBytesRead - lastBytesRead) / (DateTime.Now - lastUpdate).TotalSeconds) / 1024 / 1024;
                                    progressWindow.UpdateProgress(progress, speedMBps);
                                    WriteLog($"Progress updated: {progress:F0}%");
                                    WriteLog($"Speed updated: {speedMBps:F2} MB/s");
                                    lastUpdate = DateTime.Now;
                                    lastBytesRead = totalBytesRead;
                                }

                                Application.Current.Dispatcher.Invoke(() => { /* Keep UI responsive */ });
                            }

                            if (progressWindow.IsCancelled)
                            {
                                return;
                            }

                            progressWindow.UpdateProgress(100, 0);
                            WriteLog("Download completed.");

                            double duration = (DateTime.Now - startTime).TotalSeconds;
                            double avgSpeedMBps = contentLength.HasValue ? (contentLength.Value / duration) / 1024 / 1024 : 0;
                            WriteLog($"Download completed in {duration:F2} seconds. Average speed: {avgSpeedMBps:F2} MB/s");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error during download: {ex.Message}");
                throw;
            }
        }

        private async Task Unpack7ZipAsync(string zipFilePath, string extractPath, ProgressWindow progressWindow)
        {
            try
            {
                WriteLog("Starting 7zip unpacking.");
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                }
                Directory.CreateDirectory(extractPath);

                using (var archive = ArchiveFactory.Open(zipFilePath))
                {
                    long totalEntries = archive.Entries.Count();
                    long processedEntries = 0;

                    foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
                    {
                        if (progressWindow.IsCancelled)
                        {
                            WriteLog("Unpacking cancelled.");
                            return;
                        }

                        entry.WriteToDirectory(extractPath, new ExtractionOptions { ExtractFullPath = true, Overwrite = true });
                        processedEntries++;
                        double progress = (double)processedEntries / totalEntries * 100;
                        progressWindow.UpdateProgress(progress, 0);
                        WriteLog($"Unpacking progress: {progress:F0}%");

                        await Task.Yield(); // Keep UI responsive
                    }
                }
                WriteLog($"Unpacked 7zip to: {extractPath}");
            }
            catch (Exception ex)
            {
                WriteLog($"Error unpacking 7zip: {ex.Message}");
                throw;
            }
        }

        private async Task CreateVMAsync(string vmName, int memoryMB, int cpuCount, GalleryItem item, string extractPath, ProgressWindow progressWindow)
        {
            try
            {
                WriteLog("Starting VM creation.");
                using (PowerShell ps = PowerShell.Create())
                {
                    // Create VM
                    ps.AddCommand("New-VM")
                        .AddParameter("Name", vmName)
                        .AddParameter("MemoryStartupBytes", memoryMB * 1024L * 1024L)
                        .AddParameter("Path", vmPath)
                        .AddParameter("Generation", 2);
                    await Task.Run(() => ps.Invoke());
                    progressWindow.UpdateProgress(25, 0);
                    WriteLog($"Created VM: {vmName}");

                    if (ps.HadErrors)
                    {
                        throw new Exception(ps.Streams.Error[0].ToString());
                    }

                    // Set CPU count
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VMProcessor")
                        .AddParameter("VMName", vmName)
                        .AddParameter("Count", cpuCount);
                    await Task.Run(() => ps.Invoke());
                    progressWindow.UpdateProgress(50, 0);

                    // Set enhanced session
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VM")
                        .AddParameter("VMName", vmName)
                        .AddParameter("EnhancedSessionTransportType", item.EnhancedSessionTransportType);
                    await Task.Run(() => ps.Invoke());

                    // Set secure boot
                    ps.Commands.Clear();
                    ps.AddCommand("Set-VMFirmware")
                        .AddParameter("VMName", vmName)
                        .AddParameter("EnableSecureBoot", item.SecureBoot == "true" ? "On" : "Off");
                    await Task.Run(() => ps.Invoke());
                    progressWindow.UpdateProgress(75, 0);

                    // Move VHD to vmPath
                    string vhdSourceFile = Path.Combine(extractPath, item.ArchiveRelativePath);
                    string vhdDestFile = Path.Combine(vmPath, Path.GetFileName(item.ArchiveRelativePath));
                    if (File.Exists(vhdSourceFile))
                    {
                        WriteLog($"Moving VHD from {vhdSourceFile} to {vhdDestFile}");
                        if (File.Exists(vhdDestFile))
                        {
                            File.Delete(vhdDestFile);
                            WriteLog($"Deleted existing VHD at: {vhdDestFile}");
                        }
                        File.Move(vhdSourceFile, vhdDestFile);
                    }
                    else
                    {
                        WriteLog($"VHD not found at: {vhdSourceFile}");
                        throw new Exception($"VHD not found at {vhdSourceFile}");
                    }

                    // Attach VHD
                    if (File.Exists(vhdDestFile))
                    {
                        ps.Commands.Clear();
                        ps.AddCommand("Set-VMHardDiskDrive")
                            .AddParameter("VMName", vmName)
                            .AddParameter("Path", vhdDestFile)
                            .AddParameter("ControllerType", "SCSI");
                        await Task.Run(() => ps.Invoke());
                        WriteLog($"Attached VHD: {vhdDestFile}");
                    }
                    else
                    {
                        WriteLog($"VHD not found at: {vhdDestFile}");
                        throw new Exception($"VHD not found at {vhdDestFile}");
                    }

                    // Enable virtualization extensions if checkbox is checked
                    if (VirtualizationEnabledCheckBox.IsChecked == true)
                    {
                        ps.Commands.Clear();
                        ps.AddCommand("Set-VMProcessor")
                            .AddParameter("VMName", vmName)
                            .AddParameter("ExposeVirtualizationExtensions", true);
                        await Task.Run(() => ps.Invoke());
                        WriteLog($"Enabled virtualization extensions for VM: {vmName}");
                    }
                    else
                    {
                        WriteLog("Virtualization extensions not enabled for VM creation.");
                    }

                    progressWindow.UpdateProgress(100, 0);
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error creating VM: {ex.Message}");
                throw;
            }
        }
        private void CleanupTempFiles()
        {
            try
            {
                if (File.Exists(zipFilePath))
                {
                    File.Delete(zipFilePath);
                    WriteLog($"Deleted temporary file: {zipFilePath}");
                }
                if (Directory.Exists(extractPath))
                {
                    Directory.Delete(extractPath, true);
                    WriteLog($"Deleted temporary directory: {extractPath}");
                }
            }
            catch (Exception ex)
            {
                WriteLog($"Error cleaning up temporary files: {ex.Message}");
            }
        }
    }

    public class GalleryItem
    {
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Description { get; set; }
        public string ThumbnailUri { get; set; }
        public string LogoUri { get; set; }
        public string DiskUri { get; set; }
        public string ArchiveRelativePath { get; set; }
        public string SecureBoot { get; set; }
        public string EnhancedSessionTransportType { get; set; }
        public string Version { get; set; }
        public string LastUpdated { get; set; }
    }
}