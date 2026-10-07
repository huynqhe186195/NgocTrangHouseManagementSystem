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

        protected Floor()
        {
        }

        public Floor(
            Guid id,
            Guid buildingId,
            int floorNumber,
            string name,
            string? description = null
        ) : base(id)
        {
            BuildingId = buildingId;
            FloorNumber = floorNumber;
            Name = name;
            Description = description;
        }

        public void Update(
            int floorNumber,
            string name,
            string? description)
        {
            FloorNumber = floorNumber;
            Name = name;
            Description = description;
        }
    }
}
