using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Floors
{
    public class FloorLayoutItemDto
    {
        public Guid Id { get; set; }

        public Guid FloorId { get; set; }

        public FloorLayoutItemKind Kind { get; set; }

        public string? Label { get; set; }

        public decimal X { get; set; }

        public decimal Y { get; set; }

        public decimal Width { get; set; }

        public decimal Height { get; set; }

        public string? Points { get; set; }

        public decimal? Thickness { get; set; }

        public decimal? CornerRadius { get; set; }
    }

    public class CreateFloorLayoutItemDto
    {
        public FloorLayoutItemKind Kind { get; set; }

        [MaxLength(128)]
        public string? Label { get; set; }

        [Range(-10000, 10000)]
        public decimal X { get; set; }

        [Range(-10000, 10000)]
        public decimal Y { get; set; }

        [Range(0, 10000)]
        public decimal Width { get; set; }

        [Range(0, 10000)]
        public decimal Height { get; set; }

        [MaxLength(4000)]
        public string? Points { get; set; }

        [Range(0, 2)]
        public decimal? Thickness { get; set; }

        [Range(0, 5)]
        public decimal? CornerRadius { get; set; }
    }

    public class UpdateFloorLayoutItemDto
    {
        public FloorLayoutItemKind Kind { get; set; }

        [MaxLength(128)]
        public string? Label { get; set; }

        [Range(-10000, 10000)]
        public decimal X { get; set; }

        [Range(-10000, 10000)]
        public decimal Y { get; set; }

        [Range(0, 10000)]
        public decimal Width { get; set; }

        [Range(0, 10000)]
        public decimal Height { get; set; }

        [MaxLength(4000)]
        public string? Points { get; set; }

        [Range(0, 2)]
        public decimal? Thickness { get; set; }

        [Range(0, 5)]
        public decimal? CornerRadius { get; set; }
    }
}
