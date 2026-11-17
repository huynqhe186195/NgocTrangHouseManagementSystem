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

        // A wall is a polyline, not a box: Points holds the centreline as JSON
        // [{x,y},…] so a wall can bend around the shape of the building. Items
        // that carry no Points keep drawing from the box exactly as before.
        public string? Points { get; private set; }

        public decimal? Thickness { get; private set; }

        public decimal? CornerRadius { get; private set; }

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
            decimal height,
            string? points = null,
            decimal? thickness = null,
            decimal? cornerRadius = null) : base(id)
        {
            FloorId = floorId;
            Kind = kind;
            Label = label;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Points = points;
            Thickness = thickness;
            CornerRadius = cornerRadius;
        }

        public void Update(
            FloorLayoutItemKind kind,
            string? label,
            decimal x,
            decimal y,
            decimal width,
            decimal height,
            string? points = null,
            decimal? thickness = null,
            decimal? cornerRadius = null)
        {
            Kind = kind;
            Label = label;
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Points = points;
            Thickness = thickness;
            CornerRadius = cornerRadius;
        }
    }
}
