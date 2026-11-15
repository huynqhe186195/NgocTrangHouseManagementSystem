using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Rooms
{
    public class RoomPhoto : FullAuditedAggregateRoot<Guid>
    {
        public Guid RoomId { get; private set; }

        public string FileName { get; private set; } = default!;

        public string ContentType { get; private set; } = default!;

        public long SizeBytes { get; private set; }

        public int DisplayOrder { get; private set; }

        public string BlobName { get; private set; } = default!;

        protected RoomPhoto()
        {
        }

        public RoomPhoto(
            Guid id,
            Guid roomId,
            string fileName,
            string contentType,
            long sizeBytes,
            int displayOrder,
            string blobName) : base(id)
        {
            RoomId = roomId;
            FileName = fileName;
            ContentType = contentType;
            SizeBytes = sizeBytes;
            DisplayOrder = displayOrder;
            BlobName = blobName;
        }

        public void SetDisplayOrder(int displayOrder)
        {
            DisplayOrder = displayOrder;
        }
    }
}
