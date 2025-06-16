using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadFromRegistry : IGalleryLoader
    {
        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var items = new List<GalleryItem>();
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
                if (locations == null || locations.Length == 0)
                {
                    //WriteLog("GalleryLocations registry key not found or empty. Using default sources: Ubuntu XML, Microsoft JSON, local JSON.");
                }
                else
                {
                    //WriteLog($"Found GalleryLocations: {string.Join(", ", locations)}");
                    foreach (string location in locations)
                    {
                        if (location.StartsWith("http"))
                        {
                            items = await GalleryItem.LoadJsonFromUrl(location);
                        }
                        else if (Directory.Exists(location))
                        {
                            items = GalleryItem.LoadJsonFromFiles(location);
                        }
                        else
                        {
                            //WriteLog($"Invalid local path: {location}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                //WriteLog($"Error loading gallery items from registry: {ex.Message}");
            }
            return items;
        }
    }
}
    
