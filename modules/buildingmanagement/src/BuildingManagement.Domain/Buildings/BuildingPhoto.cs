using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Buildings
{
    /// <summary>
    /// The cover image shown on the building-management screen. A building has
    /// at most one, so <see cref="BuildingId"/> carries a unique index.
    /// Content lives in a separate row (as with room photos) so listing
    /// buildings never drags megabytes through the query.
    /// </summary>
    public class BuildingPhoto : FullAuditedAggregateRoot<Guid>
    {
        public Guid BuildingId { get; private set; }

        public string FileName { get; private set; } = default!;

        public string ContentType { get; private set; } = default!;

        public long SizeBytes { get; private set; }

        protected BuildingPhoto()
        {
        }

        public BuildingPhoto(
            Guid id,
            Guid buildingId,
            string fileName,
            string contentType,
            long sizeBytes) : base(id)
        {
            BuildingId = buildingId;
            FileName = fileName;
            ContentType = contentType;
            SizeBytes = sizeBytes;
        }
    }
}
