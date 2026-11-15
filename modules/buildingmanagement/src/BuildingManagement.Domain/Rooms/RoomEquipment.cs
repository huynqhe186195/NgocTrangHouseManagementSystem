using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Rooms
{
    public class RoomEquipment : FullAuditedAggregateRoot<Guid>
    {
        public Guid RoomId { get; private set; }

        public string Name { get; private set; } = default!;

        public string? Category { get; private set; }

        public int Quantity { get; private set; }

        public RoomEquipmentCondition Condition { get; private set; }

        public DateTime? InstalledDate { get; private set; }

        public string? Notes { get; private set; }

        protected RoomEquipment()
        {
        }

        public RoomEquipment(
            Guid id,
            Guid roomId,
            string name,
            string? category,
            int quantity,
            RoomEquipmentCondition condition,
            DateTime? installedDate = null,
            string? notes = null) : base(id)
        {
            RoomId = roomId;
            Name = name;
            Category = category;
            Quantity = quantity;
            Condition = condition;
            InstalledDate = installedDate;
            Notes = notes;
        }

        public void Update(
            string name,
            string? category,
            int quantity,
            RoomEquipmentCondition condition,
            DateTime? installedDate,
            string? notes)
        {
            Name = name;
            Category = category;
            Quantity = quantity;
            Condition = condition;
            InstalledDate = installedDate;
            Notes = notes;
        }
    }
}
