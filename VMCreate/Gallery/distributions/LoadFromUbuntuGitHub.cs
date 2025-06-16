using System.Collections.Generic;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public  class LoadFromUbuntuGitHub : IGalleryLoader
    {
        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var galleryItems = new List<GalleryItem>();
            // Load Ubuntu from repo XML 
            var items = await GalleryItem.LoadXmlFromUrl("https://raw.githubusercontent.com/canonical/ubuntu-desktop-hyper-v/master/HyperVGallery/Ubuntu-24.04.xml");
            return items;
        }
    }
}
