using System;
using System.IO;
using BuildingManagement.Images;
using Volo.Abp;

namespace BuildingManagement.Rooms
{
    /// <summary>
    /// Image acceptance is decided from the payload bytes, never from a
    /// client-declared media type or file extension. SVG is deliberately
    /// unsupported so stored content can never execute as a document.
    /// The byte rules themselves live in <see cref="ImagePayloadRules"/> so the
    /// building cover image rejects exactly the same payloads.
    /// </summary>
    public static class RoomPhotoRules
    {
        public const int MaxBytes = ImagePayloadRules.MaxBytes;

        public static string? DetectContentType(byte[] bytes) =>
            ImagePayloadRules.DetectContentType(bytes);

        public static string SafeFileName(string? fileName, string contentType)
        {
            var extension = ImagePayloadRules.ExtensionFor(contentType)
                ?? throw new BusinessException(
                    BuildingManagementErrorCodes.InvalidRoomPhotoContent);

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
