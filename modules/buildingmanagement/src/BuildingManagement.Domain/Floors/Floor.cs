using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Floors
{
    public class Floor : FullAuditedAggregateRoot<Guid>
    {
        public Guid BuildingId { get; private set; }

        public int FloorNumber { get; private set; }

        public string Name { get; private set; } = default!;

        public string? Description { get; private set; }

        public FloorStatus Status { get; private set; } = FloorStatus.Active;

        protected Floor()
        {
        }

        public Floor(
            Guid id,
            Guid buildingId,
            int floorNumber,
            string name,
            string? description = null,
            FloorStatus status = FloorStatus.Active
        ) : base(id)
        {
            BuildingId = buildingId;
            FloorNumber = floorNumber;
            Name = name;
            Description = description;
            Status = status;
        }

        /// <summary>
        /// A null <paramref name="status"/> leaves the stored value alone, so an
        /// older client that does not send the field cannot silently reset a
        /// floor that was taken out of service.
        /// </summary>
        public void Update(
            int floorNumber,
            string name,
            string? description,
            FloorStatus? status = null)
        {
            FloorNumber = floorNumber;
            Name = name;
            Description = description;
            if (status.HasValue)
            {
                Status = status.Value;
            }
        }
    }
}
