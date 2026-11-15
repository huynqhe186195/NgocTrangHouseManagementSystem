using System;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Buildings
{
    public class BuildingPhotoContent : Entity<Guid>
    {
        public byte[] Content { get; private set; } = default!;

        protected BuildingPhotoContent()
        {
        }

        public BuildingPhotoContent(Guid photoId, byte[] content) : base(photoId)
        {
            Content = content;
        }
    }
}
