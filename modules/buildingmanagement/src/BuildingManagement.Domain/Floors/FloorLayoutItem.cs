using System;
using Volo.Abp.Domain.Entities.Auditing;

namespace BuildingManagement.Floors
{
    public class FloorLayoutItem : FullAuditedAggregateRoot<Guid>
    {
        public Guid FloorId { get; private set; }

        public FloorLayoutItemKind Kind { get; private set; }

        public string? Label { get; private set; }

        public decimal X { get; private set; }

        public decimal Y { get; private set; }

        public decimal Width { get; private set; }

        public decimal Height { get; private set; }

        protected FloorLayoutItem()
        {
        }

        public FloorLayoutItem(
            Guid id,
            Guid floorId,
            FloorLayoutItemKind kind,
            string? label,
            decimal x,
            decimal y,
            decimal width,
            decimal height) : base(id)
        {
            FloorId = floorId;
            Kind = kind;
            Label = label;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public void Update(
            FloorLayoutItemKind kind,
            string? label,
            decimal x,
            decimal y,
            decimal width,
            decimal height)
        {
            Kind = kind;
            Label = label;
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
}
