using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadFromLocalJsonFile : IGalleryLoader
    {
        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var items = new List<GalleryItem>();
            // Load local JSON file
            string localJsonPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gallery.json");
            if (File.Exists(localJsonPath))
            {
                //WriteLog($"Loading local JSON from: {localJsonPath}");
                items = GalleryItem.LoadJsonFromFile(localJsonPath);
            }
            else
            {
                //WriteLog($"Local JSON file not found: {localJsonPath}");
            }
            return items;
        }
    }
}
