using System;
using Volo.Abp.Domain.Entities;

namespace BuildingManagement.Rooms
{
    public class RoomPhotoContent : Entity<Guid>
    {
        public byte[] Content { get; private set; } = default!;

        protected RoomPhotoContent()
        {
        }

        public RoomPhotoContent(Guid photoId, byte[] content) : base(photoId)
        {
            Content = content;
        }
    }
}
