using System;
using System.IO;
using Volo.Abp;

namespace BuildingManagement.Rooms
{
    /// <summary>
    /// Image acceptance is decided from the payload bytes, never from a
    /// client-declared media type or file extension. SVG is deliberately
    /// unsupported so stored content can never execute as a document.
    /// </summary>
    public static class RoomPhotoRules
    {
        public const int MaxBytes = 5 * 1024 * 1024;

        public static string? DetectContentType(byte[] bytes)
        {
            if (bytes is null || bytes.Length == 0 || bytes.Length > MaxBytes)
                return null;

            // PNG: 89 50 4E 47 0D 0A 1A 0A
            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E &&
                bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A &&
                bytes[6] == 0x1A && bytes[7] == 0x0A)
                return "image/png";

            // JPEG: FF D8 FF
            if (bytes.Length >= 3 &&
                bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "image/jpeg";

            // WebP: "RIFF" .... "WEBP"
            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 &&
                bytes[3] == 0x46 && bytes[8] == 0x57 && bytes[9] == 0x45 &&
                bytes[10] == 0x42 && bytes[11] == 0x50)
                return "image/webp";

            return null;
        }

        public static string SafeFileName(string? fileName, string contentType)
        {
            var extension = contentType switch
            {
                "image/png" => ".png",
                "image/jpeg" => ".jpg",
                "image/webp" => ".webp",
                _ => throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidRoomPhotoContent)
            };

            // Keep only the base name: a client path can never reach storage.
            var baseName = string.IsNullOrWhiteSpace(fileName)
                ? "photo"
                : Path.GetFileName(fileName.Trim());
            baseName = Path.GetFileNameWithoutExtension(baseName);
            if (baseName.Length > 120) baseName = baseName[..120];
            if (baseName.Length == 0) baseName = "photo";
            return baseName + extension;
        }
    }
}
