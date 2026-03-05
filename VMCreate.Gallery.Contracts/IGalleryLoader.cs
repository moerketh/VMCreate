using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VMCreate.Gallery
{
    public interface IGalleryLoader
    {
        Task<List<GalleryItem>> LoadGalleryItems(CancellationToken cancellationToken = default);
    }
}
