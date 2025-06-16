using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VMCreateVM;

namespace VMCreate.Gallery
{
    public class AggregateGalleryLoader : IGalleryLoader
    {
        private readonly List<IGalleryLoader> _loaders;
        private readonly IServiceProvider _serviceProvider;

        public AggregateGalleryLoader(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            _loaders = new List<IGalleryLoader>
                {  
                    //primary  
                    new LoadFromMicrosoftURI(),
                    new LoadFromRegistry(),
                    new LoadFromLocalJsonFile(),
                    //distributions  
                    _serviceProvider.GetRequiredService<LoadBlackArchCurrent>(),
                    _serviceProvider.GetRequiredService<LoadFromUbuntuGitHub>(),
                    _serviceProvider.GetRequiredService<LoadKaliCurrent>(),
                    //_serviceProvider.GetRequiredService<LoadParrotCurrent>(),
                    //_serviceProvider.GetRequiredService<LoadPentooCurrent>(),
                };
        }

        public async Task<List<GalleryItem>> LoadGalleryItems()
        {
            var allItems = new List<GalleryItem>();
            foreach (var loader in _loaders)
            {
                var items = await loader.LoadGalleryItems();
                allItems.AddRange(items);
            }
            return allItems;
        }
    }
}
