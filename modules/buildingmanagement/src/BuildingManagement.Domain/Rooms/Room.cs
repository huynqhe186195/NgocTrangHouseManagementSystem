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

        /// <summary>
        /// Free text on purpose: the letting vocabulary ("Phòng tiêu chuẩn",
        /// "Phòng gia đình", …) is the owner's, not a fixed catalogue.
        /// </summary>
        public string? RoomType { get; private set; }

        /// <summary>
        /// Where the room sits in the building, in the owner's own words
        /// ("Gần cầu thang", "View nội khu"). Free text like RoomType: it is a
        /// description for the tenant, not a coordinate the system computes.
        /// </summary>
        public string? LocationNote { get; private set; }

        public RoomDirection? Direction { get; private set; }

        public decimal? MonthlyRent { get; private set; }

        public decimal? LayoutX { get; private set; }

        public decimal? LayoutY { get; private set; }

        public decimal? LayoutWidth { get; private set; }

        public decimal? LayoutHeight { get; private set; }

        public RoomLayoutShape? LayoutShape { get; private set; }

        public decimal? LayoutRotationDegrees { get; private set; }

        public string? LayoutPoints { get; private set; }

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
            string? description = null,
            string? roomType = null,
            RoomDirection? direction = null,
            string? locationNote = null
        ) : base(id)
        {
            FloorId = floorId;
            RoomNumber = roomNumber;
            Name = name;
            Area = area;
            Capacity = capacity;
            Status = status;
            Description = description;
            RoomType = roomType;
            Direction = direction;
            LocationNote = locationNote;
        }

        public void Update(
            string roomNumber,
            string name,
            decimal? area,
            int capacity,
            RoomStatus status,
            string? description,
            string? roomType = null,
            RoomDirection? direction = null,
            string? locationNote = null)
        {
            RoomNumber = roomNumber;
            Name = name;
            Area = area;
            Capacity = capacity;
            Status = status;
            Description = description;
            RoomType = roomType;
            Direction = direction;
            LocationNote = locationNote;
        }

        public void ChangeStatus(RoomStatus status)
        {
            Status = status;
        }

        public void UpdateRent(decimal? monthlyRent)
        {
            MonthlyRent = monthlyRent;
        }

        public void UpdateLayout(
            decimal? x,
            decimal? y,
            decimal? width,
            decimal? height,
            RoomLayoutShape? shape,
            decimal? rotationDegrees,
            string? points)
        {
            LayoutX = x;
            LayoutY = y;
            LayoutWidth = width;
            LayoutHeight = height;
            LayoutShape = shape;
            LayoutRotationDegrees = rotationDegrees;
            LayoutPoints = points;
        }
    }
}
