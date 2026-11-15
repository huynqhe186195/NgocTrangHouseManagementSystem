using System;
using System.ComponentModel.DataAnnotations;

namespace BuildingManagement.Buildings
{
    public class BuildingPhotoDto
    {
        public Guid Id { get; set; }

        public Guid BuildingId { get; set; }

        public string FileName { get; set; } = default!;

        public string ContentType { get; set; } = default!;

        public long SizeBytes { get; set; }
    }

    public class BuildingPhotoFileDto
    {
        public byte[] Content { get; set; } = default!;

        public string ContentType { get; set; } = default!;

        public string FileName { get; set; } = default!;
    }

    public class UploadBuildingPhotoDto
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
}
