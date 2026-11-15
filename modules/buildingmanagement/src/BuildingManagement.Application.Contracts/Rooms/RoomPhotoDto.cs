using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Rooms
{
    public class RoomPhotoDto
    {
        public Guid Id { get; set; }

        public Guid RoomId { get; set; }

        public string FileName { get; set; } = default!;

        public string ContentType { get; set; } = default!;

        public long SizeBytes { get; set; }

        public int DisplayOrder { get; set; }
    }

    public class RoomPhotoFileDto
    {
        public byte[] Content { get; set; } = default!;

        public string ContentType { get; set; } = default!;

        public string FileName { get; set; } = default!;
    }

    public class UploadRoomPhotoDto
    {
        /// <summary>
        /// Base64 (no data-URL prefix). The declared type is ignored on
        /// purpose: the server derives the real type from the magic bytes.
        /// </summary>
        [Required]
        public string Base64Content { get; set; } = default!;

        [MaxLength(256)]
        public string? FileName { get; set; }
    }

    public class UpdateRoomLayoutDto
    {
        public decimal? X { get; set; }

        public decimal? Y { get; set; }

        public decimal? Width { get; set; }

        public decimal? Height { get; set; }

        public RoomLayoutShape? Shape { get; set; }

        public decimal? RotationDegrees { get; set; }

        public string? Points { get; set; }
    }

    public class UpdateRoomRentDto
    {
        public decimal? MonthlyRent { get; set; }
    }
}
