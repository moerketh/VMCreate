using System.Collections.Generic;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public interface IGalleryLoader
    {
        Task<List<GalleryItem>> LoadGalleryItems();
    }
}
