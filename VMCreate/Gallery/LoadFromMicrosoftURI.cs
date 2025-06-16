using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class LoadFromMicrosoftURI : IGalleryLoader
    {
        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var galleryItems = new List<GalleryItem>();
            try
            {
                // Load Microsoft Default gallery items
                galleryItems = await GalleryItem.LoadJsonFromUrl("https://go.microsoft.com/fwlink/?linkid=851584");
            }
            catch (Exception ex)
            {
                // Handle exceptions, e.g., log the error
                Console.WriteLine($"Error loading gallery items from Microsoft URI: {ex.Message}");
            }
            return galleryItems;
        }
    }
}
