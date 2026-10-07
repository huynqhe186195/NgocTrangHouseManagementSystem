using System;
using System.Collections.Generic;
using System.Text;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Rooms
{
    public class Room : FullAuditedAggregateRoot<Guid>
    {
        public Guid FloorId { get; private set; }

        public string RoomNumber { get; private set; } = default!;

        public string Name { get; private set; } = default!;

        public decimal? Area { get; private set; }

        public int Capacity { get; private set; }

        public RoomStatus Status { get; private set; }

        public string? Description { get; private set; }

        protected Room()
        {
        }

        public Room(
            Guid id,
            Guid floorId,
            string roomNumber,
            string name,
            decimal? area,
            int capacity,
            RoomStatus status,
            string? description = null
        ) : base(id)
        {
            FloorId = floorId;
            RoomNumber = roomNumber;
            Name = name;
            Area = area;
            Capacity = capacity;
            Status = status;
            Description = description;
        }

        public void Update(
            string roomNumber,
            string name,
            decimal? area,
            int capacity,
            RoomStatus status,
            string? description)
        {
            RoomNumber = roomNumber;
            Name = name;
            Area = area;
            Capacity = capacity;
            Status = status;
            Description = description;
        }
    }
}
